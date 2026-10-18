using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MediaOrganizer.App.Localization;
using MediaOrganizer.App.Services;
using MediaOrganizer.App.ViewModels;
using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Video;

namespace MediaOrganizer.App;

/// <summary>Eingebauter Betrachter für Fotos und Videos mit Vor-/Zurück-
/// Navigation innerhalb eines Ordners.
/// <para>Videos laufen über den eigenen Spieler
/// (<see cref="FfmpegVideoPlayer"/>, Bild und Ton). WPFs <c>MediaElement</c>
/// hängt an der Windows-Media-Player-Pipeline; wo die fehlt, spielt es gar
/// nichts ab — auch kein h264 — und meldet nur, dass Windows Media Player
/// erforderlich sei. Es bleibt deshalb nur der Rückfall für Rechner ohne
/// ffmpeg.</para></summary>
public partial class MediaViewerWindow : Window
{
    /// <summary>Symbole aus Segoe Fluent Icons (U+E768 Abspielen, U+E769 Pause,
    /// U+E767 Ton, U+E74F stumm). Sie stehen als Zeichen in der Datei und sind
    /// dort unsichtbar — deshalb hier benannt, damit man sie ohne Zeichentabelle
    /// wiederfindet. Die Datei muss UTF-8 bleiben; in ANSI gespeichert werden
    /// daraus stille Fragezeichen.</summary>
    private const string GlyphPlay = "";

    private const string GlyphPause = "";

    private const string GlyphVolume = "";

    private const string GlyphMuted = "";

    private readonly List<ResultItemViewModel> _items;
    private readonly SettingsStore _settingsStore = new();
    private readonly AppSettings _settings;
    private readonly string? _ffmpegPath;
    private int _index;
    private bool _isPlaying;
    private FfmpegVideoPlayer? _player;

    /// <summary>The path currently handed to <c>VideoView</c>, or null once the
    /// viewer has moved on. <c>MediaFailed</c> arrives asynchronously: without
    /// this, the failure of a video already left behind would be acted on for
    /// whatever is on screen now — and a photo handed to the ffmpeg player is
    /// reported back as an undecodable "mjpeg video".</summary>
    private string? _videoElementPath;

    public MediaViewerWindow(IEnumerable<ResultItemViewModel> items, ResultItemViewModel current)
    {
        InitializeComponent();
        _items = items.ToList();
        _index = Math.Max(0, _items.IndexOf(current));

        _settings = _settingsStore.Load();
        _ffmpegPath = FfmpegService.Locate(_settings.FfmpegPath);

        // Ton kommt sofort und ohne Zutun: Der Regler steht auf dem zuletzt
        // gewählten Wert, stummgeschaltet wird nur auf Knopfdruck.
        VolumeSlider.Value = Math.Clamp(_settings.PlaybackVolume, 0, 1);
        VideoView.Volume = VolumeSlider.Value;
        ShowMuted(false);

        Loaded += (_, _) => Show(_index);
    }

    private ResultItemViewModel Current => _items[_index];

    private void Show(int index)
    {
        if (_items.Count == 0)
        {
            return;
        }

        _index = (index + _items.Count) % _items.Count;
        var item = Current;
        var path = item.CurrentPath ?? item.Item.SourcePath;

        ResetMediaSurface();

        if (File.Exists(path))
        {
            if (item.IsVideo)
            {
                PhotoView.Visibility = Visibility.Collapsed;
                ShowPlaying(true);
                PlayButton.Visibility = Visibility.Visible;

                // Immer sichtbar, auch wo es nichts zu regeln gibt: Ein
                // fehlender Regler sieht aus wie ein Fehler, ein abgeschalteter
                // sagt, dass dieses Video keinen Ton hat.
                VolumePanel.Visibility = Visibility.Visible;
                VolumePanel.IsEnabled = true;
                VolumePanel.ToolTip = null;

                // Der eigene Spieler ist der Regelweg, nicht die Ausnahme.
                //
                // Gemessen auf dem Entwicklungsrechner: WPFs MediaElement
                // spielt dort ÜBERHAUPT kein Video ab — auch kein h264 —, weil
                // die Windows-Media-Player-Pipeline fehlt ("Windows Media
                // Player ab Version 10 ist erforderlich"). Welcher Codec in der
                // Datei steckt, ist dafür gleichgültig. Über ffmpeg verhält
                // sich die Anwendung auf jedem Zielrechner gleich, ohne dass
                // dort jemand etwas nachinstallieren muss.
                //
                // MediaElement bleibt als Rückfall für den einen Fall, in dem
                // es ffmpeg nicht gibt.
                if (_ffmpegPath is not null)
                {
                    StartPlayer(path);
                }
                else
                {
                    VideoView.Visibility = Visibility.Visible;
                    _videoElementPath = path;
                    VideoView.Source = new Uri(path);
                    VideoView.Volume = VolumeSlider.Value;
                    VideoView.Play();
                    _isPlaying = true;
                    StartOpenWatchdog(path);
                }
            }
            else
            {
                // Große Kantenlänge: scharf genug für Vollbild, ohne RAM zu fluten.
                PhotoView.Source = ImageLoading.LoadScaled(path, 2200)
                                   ?? ShellThumbnail.Get(path, 1024);
            }
        }

        Title = item.FileName;
        TitleText.Text = item.FileName;
        SubtitleText.Text = item.Item.Location is { } location
            ? Loc.Format("S.Viewer.TakenWithGps", item.Item.TakenLocal.ToString("g"), location)
            : Loc.Format("S.Tile.Taken", item.Item.TakenLocal.ToString("g"));
        DescriptionText.Text = item.Item.Description ?? "";
        DescriptionText.Visibility = string.IsNullOrWhiteSpace(item.Item.Description)
            ? Visibility.Collapsed
            : Visibility.Visible;
        CounterText.Text = $"{_index + 1} / {_items.Count}";
    }

    /// <summary>Puts the photo, video and error surfaces back into their initial
    /// state. Unsubscribing before disposing matters: a player being stopped can
    /// still report, and that report would land on the item shown next.</summary>
    private void ResetMediaSurface()
    {
        StopOpenWatchdog();

        if (_player is { } player)
        {
            player.Failed -= ShowMediaError;
            player.Dispose();
        }

        _player = null;

        VideoView.Stop();
        _videoElementPath = null;
        VideoView.Source = null;
        VideoView.Visibility = Visibility.Collapsed;
        PlayerView.Source = null;
        PlayerView.Visibility = Visibility.Collapsed;
        PhotoView.Source = null;
        PhotoView.Visibility = Visibility.Visible;
        PlayButton.Visibility = Visibility.Collapsed;
        VolumePanel.Visibility = Visibility.Collapsed;
        VolumePanel.IsEnabled = true;
        MediaErrorText.Visibility = Visibility.Collapsed;
        CodecText.Visibility = Visibility.Collapsed;
        _isPlaying = false;
    }

    /// <summary>Hält Symbol und Kurzhinweis des Knopfes am Zustand: Er zeigt,
    /// was ein Klick bewirkt, nicht was gerade läuft.</summary>
    private void ShowPlaying(bool playing)
    {
        _isPlaying = playing;
        PlayButton.Content = playing ? GlyphPause : GlyphPlay;
        PlayButton.ToolTip = Loc.Get(playing ? "S.Viewer.Pause" : "S.Viewer.Play");
    }

    private void ShowMuted(bool muted)
    {
        VideoView.IsMuted = muted;
        if (_player is { } player)
        {
            player.IsMuted = muted;
        }

        MuteButton.Content = muted ? GlyphMuted : GlyphVolume;
        MuteButton.ToolTip = Loc.Get(muted ? "S.Viewer.Unmute" : "S.Viewer.Mute");
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => ShowMuted(!VideoView.IsMuted);

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Am Regler zu ziehen heißt: hören wollen.
        VideoView.Volume = e.NewValue;
        if (_player is { } player)
        {
            player.Volume = e.NewValue;
        }

        if (VideoView.IsMuted && e.NewValue > 0)
        {
            ShowMuted(false);
        }
    }

    /// <summary>Starts ffmpeg playback (without sound).</summary>
    private void StartPlayer(string path)
    {
        StopOpenWatchdog();
        VideoView.Stop();
        _videoElementPath = null;
        VideoView.Source = null;
        VideoView.Visibility = Visibility.Collapsed;

        _player = FfmpegVideoPlayer.TryCreate(_ffmpegPath, path, PlayerView, Dispatcher);
        if (_player is null)
        {
            ShowMediaError(Loc.Get("S.Viewer.NoCodec"));
            return;
        }

        // Auch dieser Weg hat jetzt Ton — außer die Datei bringt keinen mit.
        // Dann bleibt der Regler stehen, aber abgeschaltet und mit Begründung:
        // Verschwände er, sähe es nach einem Fehler der Anwendung aus.
        _player.Volume = VolumeSlider.Value;
        _player.IsMuted = VideoView.IsMuted;
        VolumePanel.IsEnabled = _player.HasAudio;
        VolumePanel.ToolTip = _player.HasAudio ? null : Loc.Get("S.Viewer.NoAudioTrack");

        _player.Failed += ShowMediaError;
        PlayerView.Visibility = Visibility.Visible;
        CodecText.Text = string.IsNullOrEmpty(_player.AudioNote)
            ? _player.Description
            : $"{_player.Description}  ·  {_player.AudioNote}";
        CodecText.Visibility = Visibility.Visible;
        _player.Start();

        // Keep the button in step: this can also be reached by way of a failed
        // MediaElement attempt.
        ShowPlaying(true);
        PlayButton.Visibility = Visibility.Visible;
    }

    private void ShowMediaError(string message)
    {
        MediaErrorText.Text = message;
        MediaErrorText.Visibility = Visibility.Visible;
        PlayerView.Visibility = Visibility.Collapsed;
        PlayButton.Visibility = Visibility.Collapsed;
        VolumePanel.Visibility = Visibility.Collapsed;
        _isPlaying = false;
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Show(_index - 1);

    private void Next_Click(object sender, RoutedEventArgs e) => Show(_index + 1);

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_player is { } player)
        {
            if (_isPlaying)
            {
                player.Pause();
            }
            else
            {
                player.Resume();
            }

            ShowPlaying(!_isPlaying);
            return;
        }

        if (_isPlaying)
        {
            VideoView.Pause();
        }
        else
        {
            VideoView.Play();
        }

        ShowPlaying(!_isPlaying);
    }

    /// <summary>So lange bekommt der eingebaute Spieler Zeit, ein Bild zu
    /// liefern. Danach gilt er als gescheitert — auch wenn er nichts
    /// meldet.</summary>
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromMilliseconds(1500);

    private DispatcherTimer? _openWatchdog;

    /// <summary>Fehlt der Decoder, bleibt <c>MediaElement</c> schwarz und
    /// schweigt: kein <c>MediaFailed</c>, kein Fehler, nichts. Erkennbar ist es
    /// nur daran, dass nach dem Öffnen keine Bildmaße vorliegen — dann
    /// übernimmt ffmpeg.</summary>
    private void StartOpenWatchdog(string path)
    {
        StopOpenWatchdog();
        _openWatchdog = new DispatcherTimer { Interval = OpenTimeout };
        _openWatchdog.Tick += (_, _) =>
        {
            StopOpenWatchdog();

            // Nur für das Video, zu dem der Wachhund gehört: Der Betrachter
            // kann längst weitergeblättert haben.
            if (_videoElementPath == path && VideoView.NaturalVideoWidth == 0)
            {
                StartPlayer(path);
            }
        };
        _openWatchdog.Start();
    }

    private void StopOpenWatchdog()
    {
        _openWatchdog?.Stop();
        _openWatchdog = null;
    }

    private void VideoView_MediaOpened(object sender, RoutedEventArgs e)
    {
        // Bild da — der eingebaute Spieler kann diese Datei doch.
        StopOpenWatchdog();
        _isPlaying = true;
    }

    /// <summary>MediaElement fails silently (black picture). Switch to the
    /// ffmpeg player instead — but only for the video the event belongs to.
    /// The event can arrive after the viewer has moved on, and the item shown
    /// by then may well be a photo.</summary>
    private void VideoView_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (_videoElementPath is not { } path)
        {
            return;
        }

        if (File.Exists(path))
        {
            StartPlayer(path);
        }
        else
        {
            ShowMediaError(Loc.Format("S.Viewer.PlaybackFailed", e.ErrorException?.Message));
        }
    }

    private void VideoView_MediaEnded(object sender, RoutedEventArgs e)
    {
        VideoView.Position = TimeSpan.Zero;
        VideoView.Play();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                Show(_index - 1);
                break;
            case Key.Right:
            case Key.Space when !Current.IsVideo:
                Show(_index + 1);
                break;
            case Key.Escape:
                Close();
                break;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_player is { } player)
        {
            player.Failed -= ShowMediaError;
            player.Dispose();
        }

        _player = null;
        VideoView.Stop();
        VideoView.Source = null;

        // Die Lautstärke wird hier gesichert und nicht bei jedem Reglerschritt —
        // sonst schriebe ein Ziehen die Einstellungsdatei dutzendfach.
        if (Math.Abs(_settings.PlaybackVolume - VolumeSlider.Value) > 0.001)
        {
            _settings.PlaybackVolume = VolumeSlider.Value;
            _settingsStore.Save(_settings);
        }

        base.OnClosed(e);
    }
}
