using System.Diagnostics;
using System.Text;

namespace MediaOrganizer.Core.Services;

/// <summary>Diagnostic log of one processing run, written to
/// <c>%AppData%\MediaOrganizer\logs\lastrun.log</c>. Records timelines, the
/// model's raw answers, grouping decisions and the duration of every phase, so
/// that bad behaviour can be traced instead of disappearing silently into a
/// fallback.
///
/// <para>The log is always English, and deliberately not switchable. It is
/// diagnostic material, not interface text: it is searched with fixed
/// patterns, and a log whose language depended on a user setting would need
/// two sets of them.</para></summary>
public static class RunLog
{
    /// <summary>How many lines are buffered before anything is written to
    /// disk.</summary>
    private const int FlushEveryLines = 50;

    /// <summary>After this long it is written anyway — otherwise a slow run
    /// would leave the file empty for minutes, which is exactly when someone
    /// looks into it.</summary>
    private const int FlushEveryMs = 2000;

    private static readonly object Sync = new();
    private static readonly List<PhaseResult> Phases = [];

    private static StreamWriter? _writer;
    private static int _sinceFlush;
    private static long _lastFlushTick;

    /// <summary>Warnings collected during the run — failed model calls, for
    /// instance. The pipeline passes them through to the result view, which
    /// puts them into words in the interface language.</summary>
    public static List<Models.RunWarning> Warnings { get; } = [];

    public static string LogPath => Path.Combine(AppDataFolder.Root, "logs", "lastrun.log");

    public static void Start()
    {
        lock (Sync)
        {
            CloseWriter();
            Warnings.Clear();
            Phases.Clear();

            try
            {
                var path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // A permanently open writer rather than File.AppendAllText per
                // line: that opened and closed the file for EVERY line — over
                // 20 000 times on ten thousand shots, each time through the
                // virus scanner. Under a global lock it was also the bottleneck
                // standing in the way of concurrent analysis.
                //
                // FileShare.ReadWrite so the log can be read while the run is
                // still going.
                var stream = new FileStream(
                    path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
                _writer.WriteLine($"=== Run started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                _writer.Flush();
                _sinceFlush = 0;
                _lastFlushTick = Environment.TickCount64;
            }
            catch
            {
                _writer = null;
            }
        }
    }

    public static void Log(string message)
    {
        lock (Sync)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
                if (++_sinceFlush >= FlushEveryLines
                    || Environment.TickCount64 - _lastFlushTick >= FlushEveryMs)
                {
                    FlushUnlocked();
                }
            }
            catch
            {
                // Diagnostics must never disturb the run.
            }
        }
    }

    /// <summary>A warning for the user AND a line in the log. The log line
    /// carries key and arguments rather than a sentence: whoever reads it wants
    /// to know which case fired, and the wording of the interface would only
    /// change with the language setting.</summary>
    public static void Warn(Models.RunWarning warning)
    {
        lock (Sync)
        {
            Warnings.Add(warning);
        }

        Log("WARNING: " + warning);

        // Make warnings visible at once: they are the reason anyone opens the
        // log in the first place.
        Flush();
    }

    public static void Flush()
    {
        lock (Sync)
        {
            FlushUnlocked();
        }
    }

    /// <summary>Reads the log while the run is still writing to it.
    ///
    /// <para>Needed because the file stays open throughout: a plain
    /// <c>File.ReadAllText</c> asks for more exclusive access than the writer
    /// allows and fails with "used by another process".</para></summary>
    public static string ReadAllText()
    {
        try
        {
            using var stream = new FileStream(
                LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>For raw answers: cut to a manageable length.</summary>
    public static string Truncate(string text, int maxLength = 3000) =>
        text.Length <= maxLength ? text : text[..maxLength] + " …[truncated]";

    // ------------------------------------------------------------------
    // Phase timing
    // ------------------------------------------------------------------

    /// <summary>Measures how long one phase of the run takes.
    ///
    /// <para>Without these numbers any statement about speed is a guess — and
    /// guessing has gone wrong here too often. The analysis phase makes up
    /// around 93 % of a run; everything else together is under an hour on ten
    /// thousand shots.</para>
    ///
    /// <para>The number of units processed is set at the end, because that is
    /// often when it is known:</para>
    /// <code>
    /// using var phase = RunLog.Phase("Analysis");
    /// // …
    /// phase.Units = items.Count;
    /// </code></summary>
    public static PhaseScope Phase(string name) => new(name);

    /// <summary>Writes the summary of all phases at the end of the log. One
    /// single block to look at — which is what someone searches for when a run
    /// took too long.</summary>
    public static void LogPhaseSummary()
    {
        List<PhaseResult> snapshot;
        lock (Sync)
        {
            snapshot = [.. Phases];
        }

        if (snapshot.Count == 0)
        {
            return;
        }

        var total = snapshot.Aggregate(TimeSpan.Zero, (sum, p) => sum + p.Duration);
        var text = new StringBuilder();
        text.AppendLine("TIMES per phase:");
        foreach (var phase in snapshot)
        {
            var share = total > TimeSpan.Zero ? phase.Duration / total * 100 : 0;
            var perUnit = phase.Units > 0
                ? $", {phase.Duration.TotalMilliseconds / phase.Units:F0} ms per unit ({phase.Units})"
                : "";
            text.AppendLine($"  {phase.Name,-28} {Format(phase.Duration),10}  {share,5:F1} %{perUnit}");
        }

        text.Append($"  {"TOTAL",-28} {Format(total),10}");
        Log(text.ToString());
        Flush();
    }

    private static string Format(TimeSpan duration) => duration.TotalSeconds < 1
        ? $"{duration.TotalMilliseconds:F0} ms"
        : duration.TotalSeconds < 90
            ? $"{duration.TotalSeconds:F1} s"
            : $"{duration.TotalMinutes:F1} min";

    private static void Complete(string name, TimeSpan duration, int units)
    {
        lock (Sync)
        {
            Phases.Add(new PhaseResult(name, duration, units));
        }

        var perUnit = units > 0
            ? $" ({units} units, {duration.TotalMilliseconds / units:F0} ms per unit)"
            : "";
        Log($"PHASE {name}: {Format(duration)}{perUnit}");
    }

    private static void FlushUnlocked()
    {
        try
        {
            _writer?.Flush();
            _sinceFlush = 0;
            _lastFlushTick = Environment.TickCount64;
        }
        catch
        {
            // Diagnostics must never disturb the run.
        }
    }

    private static void CloseWriter()
    {
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch
        {
            // When a new run starts, a broken old writer does not matter.
        }

        _writer = null;
    }

    private readonly record struct PhaseResult(string Name, TimeSpan Duration, int Units);

    /// <summary>A running measurement of one phase; logs when disposed.</summary>
    public sealed class PhaseScope(string name) : IDisposable
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private bool _done;

        /// <summary>How many units were processed (files, groups, calls) for
        /// the "ms per unit" figure. 0 means no figure.</summary>
        public int Units { get; set; }

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            _watch.Stop();
            Complete(name, _watch.Elapsed, Units);
        }
    }
}
