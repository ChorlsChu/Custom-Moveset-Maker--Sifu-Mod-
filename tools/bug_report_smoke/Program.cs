using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using SifuMovesetEditor;

// Smoke test for the bug-report plumbing: log location/migration, retention, numbered
// rollover, tail truncation, path redaction, payload shape, and the zip bundle.
// Everything runs against a temp folder - it never touches the real app logs.

int failures = 0;
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail == null ? "" : $"  -> {detail}")}");
    if (!ok) failures++;
}

var root = Path.Combine(Path.GetTempPath(), "sifu_bugreport_smoke_" + Guid.NewGuid().ToString("N"));
var appLogs = Path.Combine(root, "app-logs");
var cwdLogs = Path.Combine(root, "logs");
Directory.CreateDirectory(appLogs);
Directory.CreateDirectory(cwdLogs);

var startedCwd = Directory.GetCurrentDirectory();
try
{
    Directory.SetCurrentDirectory(root);
    ErrorLog.LogDirectory = appLogs;

    // --- 1. stray working-directory logs are migrated, not lost -----------------
    var strayName = $"error-{DateTime.Now:yyyy-MM-dd}.log";
    File.WriteAllText(Path.Combine(cwdLogs, strayName), "STRAY-MARKER line from the old location\n");

    ErrorLog.Init();

    Check("stray log migrated into the app log dir",
        File.Exists(Path.Combine(appLogs, strayName)),
        "file not found in " + appLogs);
    Check("migrated content preserved",
        File.ReadAllText(Path.Combine(appLogs, strayName)).Contains("STRAY-MARKER"));
    Check("stray logs folder removed when empty", !Directory.Exists(cwdLogs));

    // --- 2. retention: the window is 7 days ------------------------------
    var oldName = $"error-{DateTime.Today.AddDays(-20):yyyy-MM-dd}.log";
    File.WriteAllText(Path.Combine(appLogs, oldName), "too old\n");
    var overName = $"error-{DateTime.Today.AddDays(-8):yyyy-MM-dd}.log";
    File.WriteAllText(Path.Combine(appLogs, overName), "just over a week\n");
    var keepName = $"error-{DateTime.Today.AddDays(-3):yyyy-MM-dd}.log";
    File.WriteAllText(Path.Combine(appLogs, keepName), "still useful\n");
    var edgeName = $"error-{DateTime.Today.AddDays(-7):yyyy-MM-dd}.log";
    File.WriteAllText(Path.Combine(appLogs, edgeName), "seven days exactly\n");

    ErrorLog.Init();

    Check("20-day-old log pruned", !File.Exists(Path.Combine(appLogs, oldName)));
    Check("8-day-old log pruned (7-day window)", !File.Exists(Path.Combine(appLogs, overName)));
    Check("3-day-old log kept", File.Exists(Path.Combine(appLogs, keepName)));
    Check("7-day-old log kept (boundary)", File.Exists(Path.Combine(appLogs, edgeName)));

    // --- 3. rollover numbers parts instead of overwriting ------------------------
    var bigPath = ErrorLog.CurrentLogPath;
    File.WriteAllText(bigPath, new string('x', 10 * 1024 * 1024 + 16));
    ErrorLog.WriteRaw("[ROLLOVER] after the cap");

    var part1 = Path.Combine(appLogs, $"error-{DateTime.Now:yyyy-MM-dd}.1.log");
    Check("cap rollover created a numbered part", File.Exists(part1));
    Check("rolled-over content kept in the part", File.Exists(part1) && new FileInfo(part1).Length > 10 * 1024 * 1024);
    Check("new write landed in the fresh today log",
        File.ReadAllText(bigPath).Contains("[ROLLOVER] after the cap"));

    // A second rollover must create .2, never replace .1.
    File.WriteAllText(bigPath, new string('y', 10 * 1024 * 1024 + 16));
    ErrorLog.WriteRaw("[ROLLOVER] second");
    var part2 = Path.Combine(appLogs, $"error-{DateTime.Now:yyyy-MM-dd}.2.log");
    Check("second rollover uses .2 and keeps .1", File.Exists(part2) && File.Exists(part1));

    // --- 4. tail truncation ------------------------------------------------------
    var tailMarker = "TAIL-MARKER-THIS-MUST-SURVIVE";
    File.AppendAllText(bigPath, "\n" + new string('z', 300_000) + "\n" + tailMarker + "\n");
    var recent = ErrorLog.ReadRecent(64 * 1024, 6);
    var recentText = string.Concat(recent.Select(r => r.Text));
    Check("ReadRecent returns text", recent.Count > 0);
    Check("truncated capture keeps the newest lines", recentText.Contains(tailMarker));
    Check("truncated capture respects the budget", recentText.Length <= 64 * 1024 + 15 * 1024,
        $"got {recentText.Length} chars");
    Check("files are returned oldest-first", recent.Count < 2
        || string.CompareOrdinal(recent[0].Name, recent[^1].Name) <= 0);

    // --- 3b. per-day cap: a heavy day keeps four files ---------------------------
    // Three more rollovers take the day to six files; each prune must drop the
    // lowest-numbered part, because larger numbers hold newer text.
    for (int gen = 3; gen <= 5; gen++)
    {
        ErrorLog.WriteRaw($"[GEN-{gen}] this part is generation {gen}");
        File.AppendAllText(ErrorLog.CurrentLogPath, new string('q', 10 * 1024 * 1024));
        ErrorLog.WriteRaw($"[GEN-{gen}] after the roll");
    }

    var logNameRx = new Regex(@"^error-\d{4}-\d{2}-\d{2}(?:\.\d+)?\.log$");
    string[] DayFiles() => Directory.GetFiles(appLogs, $"error-{DateTime.Now:yyyy-MM-dd}*.log")
        .Where(p => logNameRx.IsMatch(Path.GetFileName(p)))
        .ToArray();

    Check("a day holds at most 4 files", DayFiles().Length == 4,
        string.Join(", ", DayFiles().Select(Path.GetFileName)));
    Check("the oldest part is the one dropped",
        !File.Exists(Path.Combine(appLogs, $"error-{DateTime.Now:yyyy-MM-dd}.1.log")));
    Check("today's live file is never dropped", File.Exists(ErrorLog.CurrentLogPath));
    Check("the newest part survives", File.Exists(Path.Combine(appLogs, $"error-{DateTime.Now:yyyy-MM-dd}.5.log")));

    // --- 4b. folder-wide cap: 10 files, oldest day first --------------------------
    foreach (var daysAgo in new[] { 1, 2, 4, 5, 6 })
    {
        File.WriteAllText(
            Path.Combine(appLogs, $"error-{DateTime.Today.AddDays(-daysAgo):yyyy-MM-dd}.log"),
            "history\n");
    }

    ErrorLog.Init();

    var allLogs = Directory.GetFiles(appLogs, "error-*.log")
        .Where(p => logNameRx.IsMatch(Path.GetFileName(p)))
        .ToArray();
    Check("folder holds at most 10 files", allLogs.Length == 10, $"{allLogs.Length} files");
    Check("the oldest day is the one dropped",
        !File.Exists(Path.Combine(appLogs, $"error-{DateTime.Today.AddDays(-7):yyyy-MM-dd}.log")));
    Check("a recent day survives the cap",
        File.Exists(Path.Combine(appLogs, $"error-{DateTime.Today.AddDays(-1):yyyy-MM-dd}.log")));

    // --- 5. redaction ------------------------------------------------------------
    const string fakeProfile = @"C:\Users\SomebodyElse\AppData\Local\Temp\thing.uasset";
    var cleaned = BugReport.Sanitize("opened " + fakeProfile + " and D:\\Games\\Sifu\\Content\\DB",
        @"D:\Games\Sifu\Content");
    Check("profile name redacted", !cleaned.Contains("SomebodyElse"), cleaned);
    Check("profile root replaced", cleaned.Contains("<user>"), cleaned);
    Check("content root replaced", !cleaned.Contains(@"D:\Games\Sifu\Content"), cleaned);

    var realUser = Environment.UserName;
    if (realUser.Length >= 3)
    {
        var withUser = BugReport.Sanitize($"path C:\\Games\\{realUser}\\save.dat");
        Check("bare user name redacted", !withUser.Contains(realUser, StringComparison.OrdinalIgnoreCase), withUser);
    }

    // --- 5b. multi-word profile names ------------------------------------------
    // Windows profiles derived from a Microsoft account can contain a space, and the
    // whitespace-bounded match alone would leave the surname behind.
    var spaced = BugReport.Sanitize(
        @"opened C:\Users\John Smith\Documents\a.txt and again C:\Users\John Smith");
    Check("spaced profile name fully redacted", !spaced.Contains("John") && !spaced.Contains("Smith"), spaced);
    Check("spaced profile keeps the <user> root", spaced.Contains(@"C:\Users\<user>"), spaced);

    // --- 5c. install folder is redacted without the caller passing it ----------
    var appBase = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    ErrorLog.WriteRaw($"[SMOKE] app dir {appBase}\\GameConfig\\Engine\\thing.uasset");
    var installPayload = BugReport.Build("9.9", "", null, null, "bug", "install path",
        "0123456789", "", "", includeDiagnostics: false, includeLog: true);
    Check("install folder redacted from the log",
        !installPayload.Log.Contains(appBase, StringComparison.OrdinalIgnoreCase),
        installPayload.Log.Substring(0, Math.Min(300, installPayload.Log.Length)));
    Check("install folder redaction keeps the rest of the line",
        installPayload.Log.Contains(@"\GameConfig\Engine\thing.uasset"));

    // --- 6. payload shape --------------------------------------------------------
    var payload = BugReport.Build(
        appVersion: "9.9+smoke",
        webview2Version: "141.0.0.0",
        contentPath: @"D:\Games\Sifu\Content",
        outputPath: @"D:\Games\Sifu\ExportedMods",
        type: "bug",
        title: "Export aborts with 0 swaps",
        body: "Replaced the elbow move and export said it could not write the tree.",
        steps: "1. Import FD\n2. Replace a move\n3. Export",
        contact: "discord: test",
        includeDiagnostics: true,
        includeLog: true,
        context: "export aborted");

    var json = JsonConvert.SerializeObject(payload);
    Check("payload has schema 1", json.Contains("\"schema\":1"));
    Check("payload carries the log", payload.Log.Length > 0 && json.Contains("\"log\":"));
    Check("payload lists log files", payload.LogFiles.Count > 0);
    Check("payload keeps diagnostics", payload.Version == "9.9+smoke" && payload.WebView2 == "141.0.0.0");
    Check("payload is sanitized", !json.Contains(@"C:\Users\"), json.Substring(0, Math.Min(200, json.Length)));
    Check("payload fits the 256 KB log budget", payload.Log.Length <= BugReport.MaxLogChars + 16 * 1024,
        $"{payload.Log.Length} chars");
    Check("endpoint contract is https", BugReport.Endpoint.StartsWith("https://"));

    var noDiag = BugReport.Build("9.9", "", null, null, "feature", "idea", "please add x", "", "",
        includeDiagnostics: false, includeLog: false);
    Check("diagnostics toggle clears version/os", noDiag.Version == "" && noDiag.Os == "" && noDiag.Log == "");

    // --- 7. zip bundle -----------------------------------------------------------
    var zipPath = Path.Combine(root, "bundle.zip");
    var wrote = BugReport.WriteBundle(zipPath, payload, out var zipError);
    Check("bundle written", wrote, zipError);
    if (wrote)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Check("bundle has report.txt", names.Contains("report.txt"));
        Check("bundle has log entries", names.Any(n => n.StartsWith("logs/")), string.Join(",", names));
        using var reader = new StreamReader(zip.GetEntry("report.txt")!.Open());
        var reportText = reader.ReadToEnd();
        Check("report.txt carries the summary", reportText.Contains("Export aborts with 0 swaps"));
        Check("report.txt redacts paths", !reportText.Contains(@"C:\Users\"));
    }

    // --- 8. crash marker ---------------------------------------------------------
    ErrorLog.BeginSession("9.9+smoke");
    Check("session marker written", File.Exists(ErrorLog.SessionMarkerPath));
    var stale = ErrorLog.TakeStaleSessionMarker();
    Check("stale marker parsed", stale != null && stale.Version == "9.9+smoke" && stale.Pid > 0,
        stale == null ? "null" : $"pid={stale.Pid} version={stale.Version}");
    Check("marker consumed after read", !File.Exists(ErrorLog.SessionMarkerPath));
    Check("second read is empty", ErrorLog.TakeStaleSessionMarker() == null);

    ErrorLog.BeginSession("9.9+smoke");
    ErrorLog.EndSession();
    Check("clean exit leaves no marker", !File.Exists(ErrorLog.SessionMarkerPath));
}
finally
{
    Directory.SetCurrentDirectory(startedCwd);
    try { Directory.Delete(root, true); } catch { }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
