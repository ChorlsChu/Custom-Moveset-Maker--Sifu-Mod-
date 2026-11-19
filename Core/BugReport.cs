using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SifuMovesetEditor;

/// <summary>
/// One bug report, in the shape the report endpoint expects. Property names are the
/// wire contract - do not rename without bumping <see cref="Schema"/>.
/// </summary>
public sealed class BugReportPayload
{
    [JsonProperty("schema")] public int Schema { get; set; } = BugReport.Schema;
    [JsonProperty("client")] public string Client { get; set; } = "";
    [JsonProperty("version")] public string Version { get; set; } = "";
    [JsonProperty("os")] public string Os { get; set; } = "";
    [JsonProperty("arch")] public string Arch { get; set; } = "";
    [JsonProperty("dotnet")] public string Dotnet { get; set; } = "";
    [JsonProperty("webview2")] public string WebView2 { get; set; } = "";
    [JsonProperty("type")] public string Type { get; set; } = "bug";
    [JsonProperty("title")] public string Title { get; set; } = "";
    [JsonProperty("body")] public string Body { get; set; } = "";
    [JsonProperty("steps")] public string Steps { get; set; } = "";
    [JsonProperty("contact")] public string Contact { get; set; } = "";
    [JsonProperty("log")] public string Log { get; set; } = "";
    [JsonProperty("unclean_exit")] public bool UncleanExit { get; set; }
    [JsonProperty("last_fatal")] public string LastFatal { get; set; } = "";
    [JsonProperty("context")] public string Context { get; set; } = "";

    /// <summary>Per-file log text, kept out of the JSON (it is already inside <see cref="Log"/>)
    /// so the zip bundle can store the files separately.</summary>
    [JsonIgnore] public List<(string Name, string Text)> LogFiles { get; set; } = new();
}

/// <summary>
/// Builds the report body, redacts paths, and ships it either as a JSON POST or as a
/// local zip the user can attach manually. Deliberately WPF-free so the smoke harness
/// can link it: the WebView2 runtime version is passed in by the caller.
///
/// Sending is anonymous by design - the endpoint is a public, rate-limited inbox.
/// Nothing here authenticates the client, because a key baked into a distributed exe
/// is public knowledge. Treat every field as untrusted on the server side.
/// </summary>
public static class BugReport
{
    public const int Schema = 1;

    /// <summary>Portfolio-hosted inbox (Next.js route handler + D1, behind Cloudflare).</summary>
    public const string Endpoint = "https://charlestiudev.qzz.io/api/inbox/report";

    /// <summary>
    /// The inbox endpoint is live, so the dialog offers Send. Deliberately readonly (not
    /// const): the flag must stay a runtime value, otherwise setting it to false would
    /// make the whole send path unreachable code and the compiler would prune it.
    /// </summary>
    public static readonly bool SendEnabled = true;

    /// <summary>Log text cap, applied client-side (the server enforces its own cap too).</summary>
    public const int MaxLogChars = 256 * 1024;

    /// <summary>
    /// Profile segment inside a path: <c>C:\Users\Jane Doe\Documents</c>. The name may
    /// contain spaces (Windows profiles derived from a Microsoft account do), so the
    /// match runs lazily up to the next separator instead of stopping at whitespace.
    /// </summary>
    private static readonly Regex UserProfilePathRx = new(
        @"([A-Za-z]:[\\/]+Users[\\/]+)([^\\/\r\n""']+?)(?=[\\/])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The same thing when it ends the line: <c>C:\Users\Jane Doe</c>. Anchored to
    /// end-of-line so a multi-word name is taken whole instead of leaving the surname
    /// behind.
    /// </summary>
    private static readonly Regex UserProfileLineEndRx = new(
        @"([A-Za-z]:[\\/]+Users[\\/]+)([^\\/\r\n""']+?)(?=\s*$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Last resort for a profile path followed by prose on the same line
    /// (<c>C:\Users\Jane Doe is missing</c>): takes the first token, which is the given
    /// name. Only that word is guaranteed to go - matching the rest of the sentence
    /// would eat the message.
    /// </summary>
    private static readonly Regex UserProfileTokenRx = new(
        @"([A-Za-z]:[\\/]+Users[\\/]+)([^\\/\r\n""'\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Replaces the Windows profile name, the game/content root and the output folder
    /// with placeholders. Best-effort: a report is still useful with a stray path, but
    /// users should never have to hand-edit a log before sending it.
    /// </summary>
    public static string Sanitize(string? text, params string?[] extraPaths)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text;

        foreach (var raw in extraPaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var trimmed = raw.Trim().TrimEnd('\\', '/');
            if (trimmed.Length < 4) continue;
            foreach (var variant in new[]
                     {
                         trimmed,
                         trimmed.Replace('\\', '/'),
                         trimmed.Replace('/', '\\'),
                     })
            {
                if (variant.Length < 4) continue;
                s = s.Replace(variant, "<path>", StringComparison.OrdinalIgnoreCase);
            }
        }

        // In order of how much context each form can see: separator-bounded (handles a
        // multi-word name mid-line), then end-of-line, then the single token left over
        // when prose follows. Each pass is idempotent - "<user>" is already a token.
        s = UserProfilePathRx.Replace(s, "$1<user>");
        s = UserProfileLineEndRx.Replace(s, "$1<user>");
        s = UserProfileTokenRx.Replace(s, "$1<user>");

        var user = SafeUserName();
        if (user.Length >= 3)
        {
            try
            {
                s = Regex.Replace(s, @"\b" + Regex.Escape(user) + @"\b", "<user>",
                    RegexOptions.IgnoreCase);
            }
            catch { }
        }
        return s;
    }

    private static string SafeUserName()
    {
        try { return Environment.UserName ?? ""; } catch { return ""; }
    }

    /// <summary>Stable anonymous install id, created on first use. Lets the server rate
    /// limit per install instead of per IP (so a VPN does not reset the allowance).</summary>
    public static string ClientId()
    {
        try
        {
            Directory.CreateDirectory(ErrorLog.LogDirectory);
            var path = Path.Combine(ErrorLog.LogDirectory, "client.id");
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (Guid.TryParse(existing, out _)) return existing;
            }
            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, id);
            return id;
        }
        catch { return "unknown"; }
    }

    /// <summary>Human-readable summary of what the log toggle would attach.</summary>
    public static string DescribeLogCapture()
    {
        try
        {
            var files = ErrorLog.ReadRecent(MaxLogChars, 6);
            if (files.Count == 0) return "No log files yet";
            long chars = files.Sum(f => (long)f.Text.Length);
            string size = chars >= 1024 * 1024
                ? $"{chars / (1024.0 * 1024.0):F1} MB"
                : $"{Math.Max(1, chars / 1024)} KB";
            return $"{files.Count} file(s), {size}";
        }
        catch { return "Log size unavailable"; }
    }

    /// <summary>
    /// Assembles a report. <paramref name="appVersion"/> and <paramref name="webview2Version"/>
    /// come from the caller so this type stays UI-free.
    /// </summary>
    public static BugReportPayload Build(
        string appVersion,
        string webview2Version,
        string? contentPath,
        string? outputPath,
        string type,
        string title,
        string body,
        string steps,
        string contact,
        bool includeDiagnostics,
        bool includeLog,
        string? context = null,
        string? uncleanExit = null)
    {
        var payload = new BugReportPayload
        {
            Schema = Schema,
            Client = ClientId(),
            Version = includeDiagnostics ? appVersion : "",
            Os = includeDiagnostics ? SafeOs() : "",
            Arch = includeDiagnostics ? SafeArch() : "",
            Dotnet = includeDiagnostics ? SafeFramework() : "",
            WebView2 = includeDiagnostics ? webview2Version : "",
            Type = string.IsNullOrWhiteSpace(type) ? "bug" : type,
            Title = Sanitize(title),
            Body = Sanitize(body),
            Steps = Sanitize(steps),
            Contact = Sanitize(contact),
            Context = Sanitize(context),
            UncleanExit = !string.IsNullOrEmpty(uncleanExit),
        };

        if (!string.IsNullOrEmpty(uncleanExit))
            payload.Context = string.IsNullOrEmpty(payload.Context)
                ? Sanitize(uncleanExit)
                : payload.Context + "\n\n" + Sanitize(uncleanExit);

        if (!includeLog) return payload;

        // The install folder and the log folder are redacted here rather than left to the
        // caller: nearly every log line that names a file also names the exe path, which
        // would otherwise carry the user's folder layout into the report.
        var paths = new[] { contentPath, outputPath, AppContext.BaseDirectory, ErrorLog.LogDirectory };

        var sb = new StringBuilder();
        foreach (var (name, text) in ErrorLog.ReadRecent(MaxLogChars, 6))
        {
            var clean = Sanitize(text, paths);
            payload.LogFiles.Add((name, clean));
            sb.Append("----- ").Append(name).Append(" -----\n").Append(clean).Append('\n');
        }

        var crashes = ErrorLog.CrashLines(20)
            .Select(line => Sanitize(line, paths))
            .ToList();
        if (crashes.Count > 0)
        {
            payload.LastFatal = crashes[crashes.Count - 1];
            sb.Append("----- recent crashes -----\n");
            foreach (var line in crashes) sb.Append(line).Append('\n');
        }

        payload.Log = sb.ToString();
        return payload;
    }

    private static string SafeOs()
    {
        try
        {
            var desc = System.Runtime.InteropServices.RuntimeInformation.OSDescription?.Trim();
            return string.IsNullOrEmpty(desc) ? Environment.OSVersion.VersionString : desc;
        }
        catch { return Environment.OSVersion.VersionString; }
    }

    private static string SafeArch()
    {
        try { return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(); }
        catch { return ""; }
    }

    private static string SafeFramework()
    {
        try { return System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription; }
        catch { return Environment.Version.ToString(); }
    }

    /// <summary>Readable form used by the preview pane, the clipboard and report.txt.</summary>
    public static string ToText(BugReportPayload p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sifu Moveset Editor - bug report");
        sb.AppendLine($"Type      : {p.Type}");
        sb.AppendLine($"Version   : {p.Version}");
        sb.AppendLine($"OS        : {p.Os} ({p.Arch})");
        sb.AppendLine($".NET      : {p.Dotnet}");
        sb.AppendLine($"WebView2  : {p.WebView2}");
        sb.AppendLine($"Client    : {p.Client}");
        sb.AppendLine($"Unclean   : {(p.UncleanExit ? "yes" : "no")}");
        if (!string.IsNullOrEmpty(p.Contact)) sb.AppendLine($"Contact   : {p.Contact}");
        sb.AppendLine();
        sb.AppendLine("--- summary ---");
        sb.AppendLine(p.Title);
        sb.AppendLine();
        sb.AppendLine("--- what happened ---");
        sb.AppendLine(p.Body);
        if (!string.IsNullOrWhiteSpace(p.Steps))
        {
            sb.AppendLine();
            sb.AppendLine("--- steps to reproduce ---");
            sb.AppendLine(p.Steps);
        }
        if (!string.IsNullOrWhiteSpace(p.Context))
        {
            sb.AppendLine();
            sb.AppendLine("--- context ---");
            sb.AppendLine(p.Context);
        }
        if (!string.IsNullOrWhiteSpace(p.Log))
        {
            sb.AppendLine();
            sb.AppendLine("--- log ---");
            sb.AppendLine(p.Log);
        }
        return sb.ToString();
    }

    /// <summary>Writes report.txt plus the log files into a zip the user can attach anywhere.</summary>
    public static bool WriteBundle(string path, BugReportPayload payload, out string error)
    {
        error = "";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            var entry = zip.CreateEntry("report.txt", CompressionLevel.Optimal);
            using (var stream = entry.Open())
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(ToText(payload));

            foreach (var (name, text) in payload.LogFiles)
            {
                var logEntry = zip.CreateEntry("logs/" + Path.GetFileName(name), CompressionLevel.Optimal);
                using var stream = logEntry.Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(text);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>POSTs the report. Never throws: the dialog always has a fallback path.</summary>
    public static async Task<(bool Ok, string Message)> SendAsync(BugReportPayload payload)
    {
        if (!SendEnabled)
            return (false, "Sending is not enabled in this build - use Copy report or Save .zip.");

        try
        {
            var json = JsonConvert.SerializeObject(payload);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var content = new StringContent(json, new UTF8Encoding(false), "application/json");
            using var response = await http.PostAsync(Endpoint, content).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var shortText = text.Length > 200 ? text.Substring(0, 200) : text;
                return (false, $"Server returned {(int)response.StatusCode}. {shortText}".Trim());
            }

            string id = "";
            try { id = JObject.Parse(text)["id"]?.ToString() ?? ""; } catch { }
            return (true, id.Length > 0 ? $"Report {id} sent." : "Report sent.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
