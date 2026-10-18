using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MediaOrganizer.App.Localization;
using MediaOrganizer.Core.Video;
using NAudio.Wave;

namespace MediaOrganizer.App.Services;

/// <summary>Die Wiedergabe der Anwendung: ffmpeg liefert unkomprimierte
/// BGRA-Bilder über eine Pipe, ein Hintergrund-Thread taktet sie in eine
/// <see cref="WriteableBitmap"/>.
/// <para><b>Regulärer Weg, nicht Notnagel.</b> WPFs <c>MediaElement</c> hängt
/// an der Windows-Media-Player-Pipeline; fehlt sie — auf entschlackten
/// Windows-11-Systemen der Regelfall —, spielt es ÜBERHAUPT kein Video ab, auch
/// kein h264, und meldet nur "Windows Media Player ab Version 10 ist
/// erforderlich". Hier gemessen. Über ffmpeg läuft auf jedem Zielrechner
/// dasselbe, ohne dass dort etwas nachinstalliert werden muss.</para>
/// <para>Der Ton läuft getrennt: Die Tonspur wird als PCM-WAV herausgezogen und
/// über NAudio ausgegeben — den kodierten Ton kann WPF so wenig wie das Bild.
/// Das geschieht <b>nebenher</b>, während das Bild schon läuft; die Extraktion
/// vorweg ließ die Wiedergabe sekundenlang stehen. Sobald der Ton da ist,
/// springt er auf die laufende Position und wird zum <b>Taktgeber</b>: Stoppuhr
/// und Soundkarte laufen über Minuten auseinander, und sichtbar wird das an den
/// Lippen.</para></summary>
public sealed class FfmpegVideoPlayer : IDisposable
{
    /// <summary>Breite, auf die für die Wiedergabe herunterskaliert wird.
    /// 4K roh durch eine Pipe wären ~33 MB je Bild.</summary>
    private const int PlaybackWidth = 960;

    /// <summary>Die Uhr der Wiedergabe, solange kein Ton läuft. Steht still,
    /// während pausiert ist — sonst holte das Bild nach dem Fortsetzen die
    /// Pausenzeit durch Auslassen von Bildern "auf".</summary>
    private readonly Stopwatch _clock = new();

    private readonly string _ffmpegPath;
    private readonly string _videoPath;
    private readonly Image _target;
    private readonly Dispatcher _dispatcher;
    private readonly VideoStreamInfo _info;
    private readonly int _width;
    private readonly int _height;

    private readonly ManualResetEventSlim _resume = new(true);
    private CancellationTokenSource? _cts;
    private Process? _process;
    private WriteableBitmap? _bitmap;
    private Thread? _worker;

    /// <summary>Ausgabe der herausgezogenen Tonspur — über NAudio direkt auf die
    /// Windows-Audio-Schnittstelle.
    ///
    /// <para>Hier stand zuerst WPFs <see cref="MediaPlayer"/>. Der ist dieselbe
    /// Engine wie <c>MediaElement</c> und scheitert auf denselben Rechnern mit
    /// derselben Meldung ("Windows Media Player ab Version 10 ist
    /// erforderlich") — auch an einer schlichten PCM-WAV. Der Ton hing damit
    /// genau an der Pipeline, deretwegen es diesen Spieler überhaupt
    /// gibt.</para></summary>
    private WaveOutEvent? _output;

    private WaveFileReader? _reader;

    /// <summary>Position, auf die der Ton beim Einsetzen gesprungen ist. Die
    /// Geräteposition zählt ab dort bei null.</summary>
    private TimeSpan _audioStart;

    /// <summary>Bytes je Sekunde der Ausgabe — damit wird die Geräteposition
    /// (in Bytes) zu einer Zeit.</summary>
    private double _bytesPerSecond;

    /// <summary>Zwischenstufe nur für die Lautstärke: <c>WaveOutEvent.Volume</c>
    /// stellt die Systemlautstärke des Geräts, nicht die des Videos.</summary>
    private WaveChannel32? _channel;

    /// <summary>Die herausgezogene Tonspur. Bleibt über einen Neustart der
    /// Schleife bestehen — sie noch einmal herauszuziehen kostete Zeit und
    /// ergäbe dieselbe Datei.</summary>
    private string? _audioFile;

    private double _volume = 1.0;
    private bool _muted;

    private FfmpegVideoPlayer(
        string ffmpegPath, string videoPath, Image target, Dispatcher dispatcher, VideoStreamInfo info)
    {
        _ffmpegPath = ffmpegPath;
        _videoPath = videoPath;
        _target = target;
        _dispatcher = dispatcher;
        _info = info;

        // Von den ANZEIGE-Maßen ausgehen: ffmpeg dreht beim Dekodieren
        // automatisch, sodass ein hochkant gefilmtes Video als 2160×3840
        // ankommt, obwohl es als 3840×2160 kodiert ist. Rechnet man mit den
        // kodierten Maßen, wird das Bild verzerrt.
        var sourceWidth = info.DisplayWidth;
        var sourceHeight = info.DisplayHeight;
        var scale = Math.Min(1.0, PlaybackWidth / (double)Math.Max(sourceWidth, sourceHeight));

        // Gerade Kantenlängen — Anforderung der Farbraum-Umrechnung.
        _width = Math.Max(2, (int)Math.Round(sourceWidth * scale / 2) * 2);
        _height = Math.Max(2, (int)Math.Round(sourceHeight * scale / 2) * 2);
    }

    /// <summary>Fehlermeldung, wenn die Wiedergabe nicht starten konnte.</summary>
    public event Action<string>? Failed;

    public bool IsPlaying { get; private set; }

    /// <summary>Ob diese Datei überhaupt eine Tonspur mitbringt. Ohne sie sind
    /// Lautstärke und Stummschalter wirkungslos, und die Oberfläche sagt das.</summary>
    public bool HasAudio => _info.HasAudio;

    /// <summary>Lautstärke 0…1. Wirkt sofort und gilt auch für den nächsten
    /// Schleifendurchlauf.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            ApplyVolume();
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            ApplyVolume();
        }
    }

    /// <summary>Stumm ist hier Lautstärke null: Ein eigener Schalter existiert
    /// auf dieser Ebene nicht, und der Zustand liegt ohnehin in
    /// <see cref="IsMuted"/>.</summary>
    private void ApplyVolume()
    {
        if (_channel is { } channel)
        {
            channel.Volume = _muted ? 0f : (float)_volume;
        }
    }

    /// <summary>Legt einen Spieler an; null, wenn ffmpeg fehlt oder die Datei
    /// keine lesbare Videospur hat.</summary>
    public static FfmpegVideoPlayer? TryCreate(
        string? ffmpegPath, string videoPath, Image target, Dispatcher dispatcher)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            return null;
        }

        var info = VideoProbe.Read(ffmpegPath, videoPath);
        return info is null ? null : new FfmpegVideoPlayer(ffmpegPath, videoPath, target, dispatcher, info);
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
        _target.Source = _bitmap;
        _resume.Set();
        IsPlaying = true;

        _worker = new Thread(() => Run(_cts.Token))
        {
            IsBackground = true,
            Name = "FfmpegVideoPlayer",
        };
        _worker.Start();
    }

    public void Pause()
    {
        _resume.Reset();
        _clock.Stop();
        IsPlaying = false;
        _output?.Pause();
    }

    public void Resume()
    {
        _clock.Start();
        _resume.Set();
        IsPlaying = true;
        _output?.Play();
    }

    /// <summary>Zieht die Tonspur einmalig als PCM-WAV heraus. WAV, weil WPF
    /// den kodierten Ton (AAC in HEVC-Dateien) über dieselbe alte Pipeline
    /// abspielen müsste, an der schon das Bild scheitert; unkomprimiert kann
    /// sie immer.
    ///
    /// <para>Läuft auf dem Arbeits-Thread und VOR dem Bild: Beide sollen
    /// zusammen anfangen. Schlägt es fehl, läuft die Wiedergabe stumm weiter —
    /// ein fehlender Ton ist kein Grund, das Bild zu verweigern.</para></summary>
    private string? ExtractAudio(CancellationToken ct)
    {
        if (!_info.HasAudio)
        {
            return null;
        }

        if (_audioFile is { } existing && File.Exists(existing))
        {
            return existing;
        }

        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "MediaOrganizer");
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, $"audio-{Guid.NewGuid():N}.wav");

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = $"-hide_banner -loglevel error -y -i \"{_videoPath}\" " +
                            $"-vn -ac 2 -ar 48000 -c:a pcm_s16le \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return null;
            }

            process.WaitForExit(30_000);
            if (ct.IsCancellationRequested || !File.Exists(target) || new FileInfo(target).Length == 0)
            {
                return null;
            }

            _audioFile = target;
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Setzt den Ton ein, während das Bild bereits läuft: Er springt
    /// auf die Position, die das Bild gerade erreicht hat, und gibt von da an
    /// den Takt vor.</summary>
    private void StartAudio(string file, CancellationToken ct)
    {
        try
        {
            var reader = new WaveFileReader(file);
            var channel = new WaveChannel32(reader) { PadWithZeroes = false };
            var output = new WaveOutEvent();
            output.Init(channel);

            // Auf den Bildstand springen — der Ton kommt später dazu.
            var start = _clock.Elapsed;
            if (start < reader.TotalTime)
            {
                reader.CurrentTime = start;
            }

            if (ct.IsCancellationRequested)
            {
                output.Dispose();
                channel.Dispose();
                reader.Dispose();
                return;
            }

            _reader = reader;
            _channel = channel;
            _output = output;
            _audioStart = start;
            _bytesPerSecond = channel.WaveFormat.AverageBytesPerSecond;
            ApplyVolume();

            if (IsPlaying)
            {
                output.Play();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                       or FormatException or ArgumentException)
        {
            // Ohne Ton weiterlaufen — das Bild ist das Wesentliche.
            StopAudio();
        }
    }

    /// <summary>Hält den Ton an und gibt Gerät und Datei frei. Kein Sprung auf
    /// den UI-Thread nötig — NAudio bringt seinen eigenen mit.</summary>
    private void StopAudio()
    {
        var output = _output;
        var channel = _channel;
        var reader = _reader;
        _output = null;
        _channel = null;
        _reader = null;
        _bytesPerSecond = 0;
        _audioStart = TimeSpan.Zero;

        try
        {
            output?.Stop();
            output?.Dispose();
            channel?.Dispose();
            reader?.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Bereits beendet.
        }
    }

    public void Stop()
    {
        IsPlaying = false;
        _cts?.Cancel();
        _resume.Set();
        StopAudio();

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // Prozess bereits beendet.
        }

        _process?.Dispose();
        _process = null;

        // Nicht auf den Worker warten, wenn wir selbst auf dem UI-Thread sind:
        // Er kann gerade auf den Dispatcher warten — das ergäbe eine Sperre.
        if (_worker is { IsAlive: true } && Thread.CurrentThread != _worker)
        {
            _worker.Join(TimeSpan.FromMilliseconds(500));
        }

        _worker = null;
        _cts?.Dispose();
        _cts = null;
    }

    private void Run(CancellationToken ct)
    {
        try
        {
            // Der Ton wird nebenher herausgezogen. Vorweg erledigt, ließ er die
            // Wiedergabe so lange stehen, wie die Extraktion dauert — bei vier
            // Minuten Video knapp eine Sekunde, sichtbar als Ruckler vor dem
            // Start. Das Bild wartet auf nichts mehr.
            if (_info.HasAudio)
            {
                _ = Task.Run(() =>
                {
                    if (ExtractAudio(ct) is { } file && !ct.IsCancellationRequested)
                    {
                        StartAudio(file, ct);
                    }
                }, ct);
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,

                    // Zahlen invariant: mit deutschem Dezimalkomma bricht ffmpeg ab.
                    Arguments = $"-hide_banner -loglevel error -i \"{_videoPath}\" " +
                                $"-vf \"scale={_width}:{_height}\" -pix_fmt bgra -f rawvideo pipe:1",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };

            if (!process.Start())
            {
                Report(Loc.Get("S.Viewer.FfmpegStartFailed"));
                return;
            }

            _process = process;

            // stderr leeren, sonst blockiert ffmpeg an der vollen Pipe.
            var errors = new StringWriter();
            var drain = Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                    {
                        errors.WriteLine(line);
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    // Beim Stoppen erwartet.
                }
            }, ct);

            var frameBytes = _width * _height * 4;
            var buffer = new byte[frameBytes];
            var stream = process.StandardOutput.BaseStream;
            var frameInterval = TimeSpan.FromSeconds(1.0 / Math.Clamp(_info.FrameRate, 1, 120));
            _clock.Restart();
            var frameIndex = 0L;
            var rendered = false;

            // Uhr der Wiedergabe: die Stoppuhr, bis der Ton einsetzt.
            var audioLive = false;
            var audioPosition = TimeSpan.Zero;
            var sinceAudioRead = Stopwatch.StartNew();

            while (!ct.IsCancellationRequested)
            {
                _resume.Wait(ct);

                var read = 0;
                while (read < frameBytes)
                {
                    var chunk = stream.Read(buffer, read, frameBytes - read);
                    if (chunk <= 0)
                    {
                        // The pipe also ends when we kill ffmpeg ourselves — and
                        // that is not a decoding failure. Reporting it anyway put
                        // the message on whatever the viewer showed next.
                        if (ct.IsCancellationRequested)
                        {
                            return;
                        }

                        // End of the video: start over.
                        if (rendered)
                        {
                            RestartOnUiThread();
                        }
                        else
                        {
                            Report(BuildFailureMessage(errors.ToString()));
                        }

                        return;
                    }

                    read += chunk;
                }

                // Takt halten: bei Rückstand lieber Bilder auslassen als driften.
                // Läuft Ton, ist DESSEN Position die Uhr — eine eigene Stoppuhr
                // und die Soundkarte laufen über Minuten auseinander, und
                // sichtbar wird das genau dort, wo jemand hinsieht: an den
                // Lippen. Zwischen zwei Bildern wird die zuletzt gelesene
                // Position um die seither vergangene Zeit fortgeschrieben.
                var now = audioLive
                    ? audioPosition + sinceAudioRead.Elapsed
                    : _clock.Elapsed;

                var due = frameInterval * frameIndex;
                var delay = due - now;
                if (delay > TimeSpan.Zero)
                {
                    if (ct.WaitHandle.WaitOne(delay))
                    {
                        return;
                    }
                }

                frameIndex++;
                var snapshot = buffer;

                // Die Position wird im selben Sprung auf den UI-Thread gelesen,
                // in dem das Bild geschrieben wird — sonst käme je Bild ein
                // zweiter Wechsel dazu.
                // Läuft der Ton, ist SEINE Position die Uhr. Solange er steht
                // (noch nicht fertig herausgezogen, kein Gerät, Datei
                // abgelehnt), bleibt es bei der Stoppuhr — das Bild hängt nie an
                // einer Uhr, die nicht tickt.
                //
                // Gefragt ist, was das GERÄT ausgegeben hat, nicht was der Leser
                // schon gelesen hat: Gemessen läuft der Lesestand dem Hörbaren
                // um die Puffergröße voraus, hier 170–280 ms. Das Bild wäre
                // damit sichtbar vor dem Ton gelaufen.
                if (_output is { PlaybackState: PlaybackState.Playing } && _bytesPerSecond > 0)
                {
                    audioPosition = _audioStart
                                    + TimeSpan.FromSeconds(_output.GetPosition() / _bytesPerSecond);
                    sinceAudioRead.Restart();
                    audioLive = true;
                }

                _dispatcher.Invoke(() =>
                {
                    if (_bitmap is null || ct.IsCancellationRequested)
                    {
                        return;
                    }

                    _bitmap.WritePixels(
                        new System.Windows.Int32Rect(0, 0, _width, _height),
                        snapshot, _width * 4, 0);
                }, DispatcherPriority.Render, ct);
                rendered = true;
            }

            _ = drain;
        }
        catch (OperationCanceledException)
        {
            // Regulärer Abbruch.
        }
        catch (Exception ex)
        {
            Report(Loc.Format("S.Viewer.PlayFailed", ex.Message));
        }
    }

    private string BuildFailureMessage(string ffmpegErrors)
    {
        var detail = ffmpegErrors.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(detail)
            ? Loc.Format("S.Viewer.DecodeFailed", _info.Codec)
            : Loc.Format("S.Viewer.DecodeFailedDetail", _info.Codec, detail);
    }

    private void RestartOnUiThread() =>
        _dispatcher.BeginInvoke(() =>
        {
            if (_cts is { IsCancellationRequested: false })
            {
                Start();
            }
        });

    private void Report(string message) =>
        _dispatcher.BeginInvoke(() => Failed?.Invoke(message));

    public void Dispose()
    {
        Stop();
        _resume.Dispose();

        // Die herausgezogene Tonspur ist ein Zwischenstand, kein Nutzerdatum —
        // sie hat im Temp-Verzeichnis nichts verloren, sobald der Betrachter
        // weiterblättert.
        if (_audioFile is { } file)
        {
            _audioFile = null;
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Noch in Benutzung — Windows räumt Temp selbst auf.
            }
        }
    }

    /// <summary>Für die Infozeile: "HEVC · 2160×3840" (Anzeige-Maße).</summary>
    public string Description =>
        $"{_info.Codec.ToUpper(CultureInfo.CurrentCulture)} · {_info.DisplayWidth}×{_info.DisplayHeight}";

    /// <summary>Hinweis zur Tonspur — nur noch, wenn die Datei gar keine hat.
    /// Solange die Vorschau selbst keinen Ton wiedergab, stand hier auch der
    /// Fall "Datei hat Ton, du hörst ihn hier nur nicht"; den gibt es nicht
    /// mehr.</summary>
    public string AudioNote => _info.HasAudio ? "" : Loc.Get("S.Viewer.NoAudioTrack");
}
