using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SifuMovesetEditor;

/// <summary>
/// Dated, append-only diagnostic log plus the retention policy around it.
///
/// Rules the bug-report feature depends on:
///  * Logs live NEXT TO THE EXE, never in the process working directory. The old
///    <c>Directory.GetCurrentDirectory()</c> root meant a shortcut, a .bat or any other
///    launch context quietly wrote to a different "logs" folder, so a session's history
///    looked wiped when it had simply been written somewhere else.
///  * A file is never rewritten in place. Hitting the 10 MB cap moves today's file onto
///    the next numbered part (<c>error-&lt;date&gt;.1.log</c>, <c>.2</c>, ...) instead of
///    truncating it, so the half that holds the crash survives.
///  * The folder is bounded: at most <see cref="MaxFilesPerDay"/> files per day (the
///    live one included), <see cref="MaxLogFiles"/> files in total, and nothing older
///    than <see cref="KeepDays"/> days. Pruning always takes the oldest content - the
///    earliest day, then the lowest-numbered part, because parts are created in order.
/// </summary>
public static class ErrorLog
{
    /// <summary>Per-file cap: on hitting it the file rolls over to error-&lt;date&gt;.&lt;n&gt;.log.</summary>
    private const int MaxLogBytes = 10 * 1024 * 1024;

    /// <summary>A week of history: long enough for "it broke last week", short enough
    /// that a folder of logs cannot grow forever.</summary>
    private const int KeepDays = 7;

    /// <summary>Files kept per calendar day, the live file included. A day only needs a
    /// second file when the first reaches 10 MB, so four is roughly 40 MB of history.</summary>
    private const int MaxFilesPerDay = 4;

    /// <summary>Files kept in the folder across all days.</summary>
    private const int MaxLogFiles = 10;

    /// <summary>Byte ceiling, kept as a backstop for the case where ten full files are
    /// still larger than expected.</summary>
    private const long MaxTotalBytes = 100L * 1024 * 1024;

    /// <summary>
    /// Logs always live next to the exe. Settable so the smoke harness can point at a
    /// temp folder; the app itself never reassigns it.
    /// </summary>
    public static string LogDirectory { get; set; } = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "logs");

    private static readonly object WriteLock = new();
    private static string _logPath = "";
    private static string _logDate = "";
    private static DateTime _lastPruneUtc = DateTime.MinValue;

    private static readonly Regex LogNameRx = new(
        @"^error-(\d{4}-\d{2}-\d{2})(?:\.(\d+))?\.log$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Total writes this session - callers snapshot it to surface new
    /// errors (an export must not fail silently in the UI while only the log knows).</summary>
    public static int Writes;

    /// <summary>
    /// Writes whose payload looks like a real problem (thrown exception, or a
    /// WARNING/FAIL/Error marker). The export routine logs its progress through
    /// this same log, so Writes alone overstates failures - the completion panel
    /// snapshots this counter instead.
    /// </summary>
    public static int Warnings;

    /// <summary>Today's log file (created on first write).</summary>
    public static string CurrentLogPath =>
        Path.Combine(LogDirectory, $"error-{DateTime.Now:yyyy-MM-dd}.log");

    /// <summary>Written at startup and deleted on a clean exit; see <see cref="TakeStaleSessionMarker"/>.</summary>
    public static string SessionMarkerPath => Path.Combine(LogDirectory, "session.running");

    public static void Init()
    {
        int migrated = 0;
        try
        {
            Directory.CreateDirectory(LogDirectory);
            migrated = MigrateStrayLogs();
            PruneOldLogs();
            lock (WriteLock)
            {
                RollIfNecessary();
                File.AppendAllText(_logPath,
                    $"===== session start {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\n");
                if (migrated > 0)
                {
                    File.AppendAllText(_logPath,
                        $"[{DateTime.Now:HH:mm:ss}] [LOG] moved {migrated} log file(s) from a stray working-directory logs folder\n");
                }
            }
        }
        catch { }
        Writes = 0;
        Warnings = 0;
    }

    public static void Write(string tag, Exception ex)
    {
        Interlocked.Increment(ref Writes);
        if (LooksLikeWarning(ex)) Interlocked.Increment(ref Warnings);
        Append($"[{DateTime.Now:HH:mm:ss}] [{tag}] {ex}\n");
    }

    public static void Write(string tag, string message)
    {
        Interlocked.Increment(ref Writes);
        if (MessageLooksLikeWarning(message)) Interlocked.Increment(ref Warnings);
        Append($"[{DateTime.Now:HH:mm:ss}] [{tag}] {message}\n");
    }

    /// <summary>Debug/progress line (AnimationParser.LogDebug): lands in the same file,
    /// but deliberately does not move the Writes/Warnings counters - those drive the
    /// export warning panel and historical debug lines never counted toward them.</summary>
    public static void WriteRaw(string line)
        => Append($"[{DateTime.Now:HH:mm:ss}] {line}\n");

    private static void Append(string msg)
    {
        lock (WriteLock)
        {
            try
            {
                RollIfNecessary();
                File.AppendAllText(_logPath, msg);
            }
            catch { }
        }
    }

    /// <summary>Resolves today's dated file (date rollover or first write), enforces the
    /// size cap, and triggers the periodic prune. Always called under WriteLock.</summary>
    private static void RollIfNecessary()
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_logDate != today || _logPath.Length == 0)
        {
            Directory.CreateDirectory(LogDirectory);
            _logDate = today;
            _logPath = Path.Combine(LogDirectory, $"error-{today}.log");
        }
        else if (new FileInfo(_logPath).Length >= MaxLogBytes)
        {
            // Number the parts instead of replacing ".1": a heavy session must never
            // delete the earlier half of the day, which is where a crash usually is.
            //
            // Numbers are never reused (max + 1, not the first free slot). Pruning and
            // ReadRecent both rely on larger part = newer content, and reusing a freed
            // number after a prune would put the newest file at the oldest position.
            int next = 1;
            foreach (var existing in Directory.GetFiles(LogDirectory, $"error-{today}.*.log"))
            {
                var m = LogNameRx.Match(Path.GetFileName(existing));
                if (m.Success && m.Groups[2].Success
                    && int.TryParse(m.Groups[2].Value, out var n) && n >= next)
                    next = n + 1;
            }
            var backup = Path.Combine(LogDirectory, $"error-{today}.{next}.log");

            try { File.Move(_logPath, backup); }
            catch { return; } // cannot roll: keep appending rather than lose the next line

            try
            {
                File.AppendAllText(_logPath,
                    $"[{DateTime.Now:HH:mm:ss}] [LOG] Hit the {MaxLogBytes / (1024 * 1024)} MB cap - "
                    + $"the earlier part is kept as {Path.GetFileName(backup)}\n");
            }
            catch { }

            // Enforce the four-files-per-day cap right now instead of waiting for the
            // hourly prune: a runaway session must not fill the folder between prunes.
            PruneOldLogs();
            _lastPruneUtc = DateTime.UtcNow;
        }

        if ((DateTime.UtcNow - _lastPruneUtc).TotalMinutes >= 60)
        {
            _lastPruneUtc = DateTime.UtcNow;
            PruneOldLogs();
        }
    }

    /// <summary>
    /// Moves error-*.log files out of a "logs" folder in the current working directory
    /// (where pre-fix builds wrote them) into <see cref="LogDirectory"/>. Never
    /// overwrites, never throws.
    /// </summary>
    private static int MigrateStrayLogs()
    {
        try
        {
            var cwd = Path.GetFullPath(Directory.GetCurrentDirectory()).TrimEnd('\\');
            var appDir = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');
            if (string.Equals(cwd, appDir, StringComparison.OrdinalIgnoreCase)) return 0;

            var stray = Path.Combine(cwd, "logs");
            if (!Directory.Exists(stray)) return 0;

            int moved = 0;
            foreach (var file in Directory.GetFiles(stray, "error-*.log"))
            {
                if (!LogNameRx.IsMatch(Path.GetFileName(file))) continue;
                var dest = Path.Combine(LogDirectory, Path.GetFileName(file));
                if (File.Exists(dest)) continue;
                try { File.Move(file, dest); moved++; } catch { }
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(stray).Any())
                    Directory.Delete(stray);
            }
            catch { }

            return moved;
        }
        catch { return 0; }
    }

    private static void PruneOldLogs()
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return;

            // Before the first roll of a process _logPath is empty; CurrentLogPath is the
            // file Init is about to append to, so it is protected either way.
            var live = Path.GetFullPath(_logPath.Length > 0 ? _logPath : CurrentLogPath);

            var cutoff = DateTime.Today.AddDays(-KeepDays);
            var kept = new List<(FileInfo Info, DateTime Date, int Part, long Length)>();

            foreach (var file in new DirectoryInfo(LogDirectory).GetFiles("error-*.log"))
            {
                var date = ParseLogDate(file.Name);
                if (date == null) continue;

                if (date.Value < cutoff)
                {
                    if (!IsLive(file, live)) { try { file.Delete(); } catch { } }
                    continue;
                }
                kept.Add((file, date.Value, ParsePart(file.Name), file.Length));
            }

            // Oldest content first: earliest day, then the lowest-numbered part, because
            // larger part numbers are created later and therefore hold newer text.
            bool DropOldest(IEnumerable<(FileInfo Info, DateTime Date, int Part, long Length)> pool)
            {
                foreach (var entry in pool.OrderBy(e => e.Date).ThenBy(e => e.Part))
                {
                    if (IsLive(entry.Info, live)) continue;
                    try { entry.Info.Delete(); } catch { continue; }
                    kept.Remove(entry);
                    return true;
                }
                return false;
            }

            // Per-day cap. A day normally has one file; the others only exist after the
            // 10 MB rollover, so this keeps the most recent ~40 MB of a long session.
            foreach (var date in kept.Select(k => k.Date).Distinct().ToList())
            {
                while (kept.Count(k => k.Date == date) > MaxFilesPerDay)
                    if (!DropOldest(kept.Where(k => k.Date == date))) break;
            }

            // Folder-wide cap across days.
            while (kept.Count > MaxLogFiles)
                if (!DropOldest(kept)) break;

            // Byte ceiling as a backstop.
            long total = kept.Sum(k => k.Length);
            if (total <= MaxTotalBytes) return;
            foreach (var entry in kept.OrderBy(e => e.Date).ThenBy(e => e.Part).ToList())
            {
                if (total <= MaxTotalBytes) break;
                if (IsLive(entry.Info, live)) continue;
                try
                {
                    entry.Info.Delete();
                    total -= entry.Length;
                }
                catch { }
            }
        }
        catch { }
    }

    private static bool IsLive(FileInfo file, string livePath)
        => string.Equals(file.FullName, livePath, StringComparison.OrdinalIgnoreCase);

    private static DateTime? ParseLogDate(string fileName)
    {
        var m = LogNameRx.Match(fileName);
        if (!m.Success) return null;
        return DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    /// <summary>
    /// Recent log text for a bug report: newest files first, each trimmed to its tail,
    /// so a 10 MB file contributes only what fits. Returned oldest-first so the bundle
    /// reads chronologically.
    /// </summary>
    public static List<(string Name, string Text)> ReadRecent(
        int maxTotalChars = 256 * 1024, int maxFiles = 6)
    {
        var result = new List<(string Name, string Text)>();
        try
        {
            if (!Directory.Exists(LogDirectory)) return result;

            var files = new DirectoryInfo(LogDirectory)
                .GetFiles("error-*.log")
                .Select(f => (Info: f, Date: ParseLogDate(f.Name), Part: ParsePart(f.Name)))
                .Where(x => x.Date != null)
                .OrderByDescending(x => x.Date!.Value)
                // Within a day the live file is part 0 and .1/.2 are older, so ascending
                // part = newest content first. (Reversed below for chronological order.)
                .ThenBy(x => x.Part)
                .Take(Math.Max(1, maxFiles))
                .ToList();

            int remaining = Math.Max(1024, maxTotalChars);
            foreach (var f in files)
            {
                if (remaining <= 512) break;
                var text = ReadTail(f.Info.FullName, remaining);
                if (string.IsNullOrEmpty(text)) continue;
                remaining -= text.Length;
                result.Add((f.Info.Name, text));
            }
        }
        catch { }

        result.Reverse();
        return result;
    }

    /// <summary>Every FATAL / swallowed-UI-exception / unhandled line still on disk, oldest first.</summary>
    public static List<string> CrashLines(int max = 40)
    {
        var lines = new List<string>();
        foreach (var (name, text) in ReadRecent(512 * 1024, 6))
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.Contains("[FATAL]", StringComparison.Ordinal)
                    || line.Contains("[UI]", StringComparison.Ordinal)
                    || line.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase))
                    lines.Add($"{name}: {line}");
            }
        }
        return lines.Count <= max ? lines : lines.Skip(lines.Count - max).ToList();
    }

    private static int ParsePart(string fileName)
    {
        var m = LogNameRx.Match(fileName);
        if (!m.Success || !m.Groups[2].Success) return 0;
        return int.TryParse(m.Groups[2].Value, out var part) ? part : 0;
    }

    private static string ReadTail(string path, int maxChars)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long length = fs.Length;
            int take = (int)Math.Min(length, maxChars);
            if (take <= 0) return "";
            fs.Seek(-take, SeekOrigin.End);
            var buffer = new byte[take];
            int read = fs.Read(buffer, 0, take);
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            if (take < length)
            {
                // Drop the partial first line created by starting mid-file.
                int nl = text.IndexOf('\n');
                if (nl >= 0) text = text.Substring(nl + 1);
            }
            return text;
        }
        catch { return ""; }
    }

    /// <summary>Thrown exceptions carry a stack trace; progress lines are fresh
    /// Exception objects built for the log and never thrown.</summary>
    private static bool LooksLikeWarning(Exception ex)
        => ex.StackTrace != null || MessageLooksLikeWarning(ex.Message);

    private static readonly Regex _warningMarker =
        new(@"\bWARNING\b|\bFAIL\b|\bError\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Word-boundary match: "FLOW WARNING:" counts, but progress lines that
    /// merely mention a count ("... 0 flow warnings ...") do not.</summary>
    private static bool MessageLooksLikeWarning(string? message)
        => message != null && _warningMarker.IsMatch(message);

    // ---- crash marker ----------------------------------------------------------

    /// <summary>Contents of a session.running file left behind by a session that died.</summary>
    public sealed record SessionMarker(string Started, int Pid, string Version);

    /// <summary>Records that a session is running. Deleted by <see cref="EndSession"/> on a clean exit.</summary>
    public static void BeginSession(string version)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.WriteAllText(SessionMarkerPath,
                $"started={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\npid={Environment.ProcessId}\nversion={version}\n");
        }
        catch { }
    }

    public static void EndSession()
    {
        try { File.Delete(SessionMarkerPath); } catch { }
    }

    /// <summary>
    /// Returns the previous session's marker when the app did not shut down cleanly,
    /// otherwise null. The marker is consumed either way so the prompt shows once.
    /// </summary>
    public static SessionMarker? TakeStaleSessionMarker()
    {
        try
        {
            if (!File.Exists(SessionMarkerPath)) return null;
            var text = File.ReadAllText(SessionMarkerPath);
            File.Delete(SessionMarkerPath);

            string started = "", version = "";
            int pid = 0;
            foreach (var line in text.Split('\n'))
            {
                var idx = line.IndexOf('=');
                if (idx <= 0) continue;
                var key = line.Substring(0, idx).Trim();
                var value = line.Substring(idx + 1).Trim();
                if (key == "started") started = value;
                else if (key == "pid") int.TryParse(value, out pid);
                else if (key == "version") version = value;
            }
            return new SessionMarker(started, pid, version);
        }
        catch { return null; }
    }
}
