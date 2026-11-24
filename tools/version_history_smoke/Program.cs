using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SifuMovesetEditor;

namespace version_history_smoke;

/// <summary>
/// Pins the version-history plumbing without touching the network: release-list
/// filtering/dedupe/order, the "newest newer than me" rule the launch check uses,
/// the pre-install backup and its pruning. Exit code 0 = all passed.
/// </summary>
internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--screenshot")
            return RenderScreenshot(args[1]);

        Console.WriteLine("== version history ==");
        CheckParse();
        CheckNewest();
        CheckBackup();
        CheckSwap();
        CheckPrune();
        CheckDialog();

        Console.WriteLine(_failures == 0
            ? "ALL VERSION HISTORY SMOKE TESTS PASSED"
            : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void CheckParse()
    {
        Console.WriteLine("  -- release list --");

        const string json = """
        [
          {"tag_name":"v1.4","draft":false,"prerelease":false,"body":"notes 1.4",
           "published_at":"2026-11-19T09:30:00Z",
           "assets":[{"name":"SifuMovesetEditor.zip","browser_download_url":"https://example/1.4.zip","size":1048576}]},
          {"tag_name":"v1.5","draft":true,"prerelease":false,"body":"draft",
           "assets":[{"name":"SifuMovesetEditor.zip","browser_download_url":"https://example/1.5.zip","size":1}]},
          {"tag_name":"v1.3-rc","draft":false,"prerelease":true,"body":"rc",
           "assets":[{"name":"SifuMovesetEditor.zip","browser_download_url":"https://example/rc.zip","size":1}]},
          {"tag_name":"v1.3","draft":false,"prerelease":false,"body":"notes 1.3",
           "published_at":"2026-10-20T12:00:00Z",
           "assets":[{"name":"SifuMovesetEditor.zip","browser_download_url":"https://example/1.3.zip","size":2097152}]},
          {"tag_name":"V1.3","draft":false,"prerelease":false,"body":"duplicate tag",
           "assets":[{"name":"SifuMovesetEditor.zip","browser_download_url":"https://example/1.3-again.zip","size":1}]},
          {"tag_name":"v1.2","draft":false,"prerelease":false,"body":"no asset",
           "assets":[{"name":"something-else.zip","browser_download_url":"https://example/other.zip","size":1}]},
          {"tag_name":"nightly","draft":false,"prerelease":false,"body":"unparsable",
           "assets":[{"name":"SifuMovesetEditor.zip","browser_download_url":"https://example/n.zip","size":1}]}
        ]
        """;

        var releases = UpdateChecker.ParseReleases(json);
        Check(releases.Count == 2, $"drafts, prereleases, assets-less and bad tags are dropped (kept {releases.Count})");
        Check(releases.Select(r => r.Version).SequenceEqual(new[] { new Version(1, 4), new Version(1, 3) }),
            "releases are sorted newest first");
        Check(releases[0].Notes == "notes 1.4", "release notes survive parsing");
        Check(releases[0].AssetUrl == "https://example/1.4.zip", "download URL is taken from the zip asset");
        Check(releases[0].AssetSize == 1048576, "asset size is captured");
        Check(releases[0].PublishedAt != null && releases[0].PublishedAt.Value.Year == 2026,
            "publish date is parsed");
        Check(releases.Count(r => r.Version == new Version(1, 3)) == 1,
            "a repeated tag is listed once");

        Check(UpdateChecker.ParseReleases("[]").Count == 0, "an empty release list is fine");
        Check(UpdateChecker.ParseReleases(null).Count == 0, "null json is treated as no releases");
    }

    private static void CheckNewest()
    {
        Console.WriteLine("  -- launch check --");
        var releases = UpdateChecker.ParseReleases("""
        [
          {"tag_name":"v1.4","assets":[{"name":"SifuMovesetEditor.zip","browser_download_url":"https://example/1.4.zip"}]},
          {"tag_name":"v1.3","assets":[{"name":"SifuMovesetEditor.zip","browser_download_url":"https://example/1.3.zip"}]}
        ]
        """);

        var onOlder = UpdateChecker.FindNewest(releases, new Version(1, 2));
        Check(onOlder?.Version == new Version(1, 4), "an older build is offered the newest release");
        Check(UpdateChecker.FindNewest(releases, new Version(1, 4)) == null,
            "running the newest release offers nothing");
        Check(UpdateChecker.FindNewest(releases, new Version(1, 5)) == null,
            "a local build ahead of every release is not offered a downgrade");
    }

    private static void CheckBackup()
    {
        Console.WriteLine("  -- pre-install backup --");
        var root = Path.Combine(Path.GetTempPath(), "version_history_smoke_" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "app");
        var payload = Path.Combine(root, "payload");
        try
        {
            Write(app, "SifuMovesetEditor.exe", "old exe");
            Write(app, "Core.dll", "old core");
            Write(app, "settings.json", "user settings");
            Write(app, Path.Combine("CustomAssets", "thing.uasset"), "library file");
            Write(app, Path.Combine("GameContent", "game.pak"), "game content");
            Write(app, Path.Combine("ExportedMods", "mod.pak"), "exported mod");

            Write(payload, "SifuMovesetEditor.exe", "new exe");
            Write(payload, "Core.dll", "new core");
            Write(payload, "NewOnly.dll", "only in the new release");
            Write(payload, "settings.json", "package settings");
            Write(payload, Path.Combine("CustomAssets", "thing.uasset"), "packaged library");
            Write(payload, Path.Combine("GameContent", "game.pak"), "packaged game content");
            Write(payload, Path.Combine("ExportedMods", "mod.pak"), "packaged mod");

            var result = UpdateChecker.BackupInstall(payload, app, new Version(1, 4));
            Check(result.Created, "a backup folder is created");
            Check(result.Copied == 2, $"only files the payload replaces are copied (got {result.Copied})");
            Check(result.Skipped == 0, "nothing is skipped for readable files");
            if (result.Dir != null)
            {
                Check(File.ReadAllText(Path.Combine(result.Dir, "SifuMovesetEditor.exe")) == "old exe",
                    "backup keeps the pre-install bytes");
                Check(!File.Exists(Path.Combine(result.Dir, "NewOnly.dll")),
                    "files the install adds are not in the backup");
                Check(!File.Exists(Path.Combine(result.Dir, "settings.json")),
                    "settings.json is excluded (the swap never touches it)");
                Check(!Directory.Exists(Path.Combine(result.Dir, "CustomAssets")),
                    "user data is excluded from the backup");
                Check(!Directory.Exists(Path.Combine(result.Dir, "GameContent")),
                    "game content is excluded from the backup");
                Check(!Directory.Exists(Path.Combine(result.Dir, "ExportedMods")),
                    "exported mods are excluded from the backup");
                Check(File.Exists(Path.Combine(result.Dir, "RESTORE.txt")),
                    "RESTORE.txt is written next to the backup");
                var restore = Path.Combine(result.Dir, "RESTORE.txt");
                if (File.Exists(restore))
                    Check(File.ReadAllText(restore).Contains("Replace the files", StringComparison.Ordinal),
                        "RESTORE.txt explains the copy-back steps");
                Check(Path.GetFileName(result.Dir).StartsWith("v1.4-", StringComparison.Ordinal),
                    "backup folder is named after the version being replaced");

                var manifestPath = Path.Combine(result.Dir, "backup.json");
                Check(File.Exists(manifestPath), "backup.json is written");
                if (File.Exists(manifestPath))
                {
                    var manifest = File.ReadAllText(manifestPath);
                    Check(manifest.Contains("\"version\": \"1.4\"", StringComparison.Ordinal),
                        "backup.json records the installed version");
                    Check(manifest.Contains("\"copied\": 2", StringComparison.Ordinal),
                        "backup.json records the file count");
                }
            }

            var fresh = Path.Combine(root, "fresh");
            Directory.CreateDirectory(fresh);
            var nothing = UpdateChecker.BackupInstall(payload, fresh, new Version(1, 4));
            Check(!nothing.Created, "a fresh install with nothing to replace creates no backup");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>A downgrade lays the older payload over the newer tree. This pins
    /// what that does to user files (nothing) and to files only the newer
    /// release shipped (they stay, inert - the swap never deletes).</summary>
    private static void CheckSwap()
    {
        Console.WriteLine("  -- downgrade swap --");
        var root = Path.Combine(Path.GetTempPath(), "version_history_swap_" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "app");
        var payload = Path.Combine(root, "payload");
        try
        {
            Write(app, "SifuMovesetEditor.exe", "v1.4 exe");
            Write(app, "Core.dll", "v1.4 core");
            Write(app, "NewerOnly.dll", "only in v1.4");
            Write(app, "settings.json", "user settings");
            Write(app, Path.Combine("CustomAssets", "thing.uasset"), "library file");
            Write(app, Path.Combine("ExportedMods", "mod.pak"), "exported mod");
            Write(app, Path.Combine("GameContent", "game.pak"), "game content");
            Write(app, Path.Combine("logs", "update.log"), "user log");

            Write(payload, "SifuMovesetEditor.exe", "v1.2 exe");
            Write(payload, "Core.dll", "v1.2 core");
            Write(payload, "settings.json", "packaged settings");
            Write(payload, Path.Combine("CustomAssets", "thing.uasset"), "packaged library");
            Write(payload, Path.Combine("ExportedMods", "mod.pak"), "packaged mod");
            Write(payload, Path.Combine("GameContent", "game.pak"), "packaged game content");
            Write(payload, Path.Combine("logs", "update.log"), "packaged log");

            UpdateChecker.SwapIntoPlace(payload, app);

            Check(File.ReadAllText(Path.Combine(app, "SifuMovesetEditor.exe")) == "v1.2 exe",
                "the older exe replaces the newer one");
            Check(File.ReadAllText(Path.Combine(app, "Core.dll")) == "v1.2 core",
                "the older dependency replaces the newer one");
            Check(File.ReadAllText(Path.Combine(app, "settings.json")) == "user settings",
                "settings.json is never overwritten by a swap");
            Check(File.ReadAllText(Path.Combine(app, "CustomAssets", "thing.uasset")) == "library file",
                "the custom library survives a downgrade, even if the payload carries it");
            Check(File.ReadAllText(Path.Combine(app, "ExportedMods", "mod.pak")) == "exported mod",
                "exported mods survive a downgrade, even if the payload carries them");
            Check(File.ReadAllText(Path.Combine(app, "GameContent", "game.pak")) == "game content",
                "game content survives a downgrade, even if the payload carries it");
            Check(File.ReadAllText(Path.Combine(app, "logs", "update.log"))
                    .StartsWith("user log", StringComparison.Ordinal),
                "the logs folder is not replaced (the swap only appends its own line)");
            Check(File.Exists(Path.Combine(app, "NewerOnly.dll")),
                "files only the newer release shipped are left behind (inert, never deleted)");
            Check(UpdateChecker.IsUserDataPath(Path.Combine("GameContent", "x.pak")),
                "game content is classified as user data");
            Check(UpdateChecker.IsUserDataPath("settings.json"),
                "settings.json is classified as user data");
            Check(!UpdateChecker.IsUserDataPath(Path.Combine("viewer", "index.html")),
                "app assets are still replaced");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void CheckPrune()
    {
        Console.WriteLine("  -- backup retention --");
        var root = Path.Combine(Path.GetTempPath(), "version_history_prune_" + Guid.NewGuid().ToString("N"));
        try
        {
            var backups = Path.Combine(root, UpdateChecker.BackupDirName);
            Directory.CreateDirectory(backups);
            string[] stamps = { "20260101-000001", "20260102-000001", "20260103-000001", "20260104-000001", "20260105-000001" };
            foreach (var stamp in stamps)
                Directory.CreateDirectory(Path.Combine(backups, $"v1.4-{stamp}"));

            var removed = UpdateChecker.PruneBackups(root, 3);
            Check(removed == 2, $"older backups are removed (got {removed})");

            var remaining = Directory.GetDirectories(backups)
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            Check(remaining.Length == 3, "three backups are kept");
            Check(remaining[0] == "v1.4-20260103-000001" && remaining[2] == "v1.4-20260105-000001",
                "the newest three are the ones kept");

            Check(UpdateChecker.PruneBackups(Path.Combine(root, "missing"), 3) == 0,
                "pruning a folder with no backups is a no-op");
            Check(UpdateChecker.BackupStamp("v1.4-20261120-143012") == "20261120-143012",
                "the timestamp is read out of the folder name");
            Check(UpdateChecker.BackupStamp("scratch") == "00000000-000000",
                "names without a timestamp sort last");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void CheckDialog()
    {
        Console.WriteLine("  -- dialog --");
        if (System.Windows.Application.Current == null)
            _ = new System.Windows.Application();

        var dlg = new SifuMovesetEditor.Update.VersionHistoryDialog(autoLoad: false)
        {
            ShowInTaskbar = false,
            WindowStyle = System.Windows.WindowStyle.None,
            ShowActivated = false,
            Left = -10000,
            Top = -10000,
        };

        try
        {
            // Show first so the ListBox has real containers; autoLoad is off, so
            // this never touches GitHub.
            dlg.Show();
            dlg.ShowReleases(new List<UpdateInfo>
            {
                Make("v1.4", "1.4", 2026, 11, 19, "- notes", 41_000_000),
                Make("v1.3", "1.3", 2026, 10, 20,
                    "## What's new\n- Enemy graphs keep one probability pool\n" +
                    "- Faster thumbnail rendering\n- Fixed a crash when a stance had no variants\n" +
                    "- Cleaned up the export warning list\n- Added a missing null check on import\n" +
                    "- Rebalanced the chance sums on export", 40_000_000),
                Make("v1.2", "1.2", 2026, 9, 12, "- Import Mod merge options", 39_000_000),
                Make("v1.1", "1.1", 2026, 8, 3, "- notes", 38_000_000),
            });
            Pump();
            var layoutAtOpen = CaptureLayout(dlg);

            dlg.SelectReleaseForTest(2);
            Pump();
            CheckLayoutStable(dlg, layoutAtOpen, "selecting a pre-1.3 release");
            Check(dlg.SelectedReleaseIndexForTest == 2,
                "selecting an older release sticks after the layout pass");
            Check(dlg.SelectedRequiresBackupForTest, "a pre-1.3 selection requires a backup");
            Check(dlg.BackupVisibleForTest, "the backup box is shown for a downgrade");
            Check(dlg.BackupCheckedForTest && !dlg.BackupEnabledForTest,
                "the pre-1.3 backup box is checked and locked");

            dlg.SelectReleaseForTest(1);
            Pump();
            CheckLayoutStable(dlg, layoutAtOpen, "selecting v1.3 from v1.2");
            Check(dlg.SelectedReleaseIndexForTest == 1 && !dlg.SelectedRequiresBackupForTest,
                "v1.3 stays selected and its backup is not forced");

            var style = (System.Windows.Style)dlg.FindResource("ReleaseItem");
            var foreground = style.Setters.OfType<System.Windows.Setter>()
                .FirstOrDefault(s => s.Property == System.Windows.Controls.Control.ForegroundProperty);
            Check(foreground?.Value is System.Windows.Media.SolidColorBrush brush &&
                  brush.Color == System.Windows.Media.Color.FromRgb(0xcd, 0xd6, 0xf4),
                "release rows use the light foreground instead of the theme's black");
            Check(UpdateChecker.FirstAutoUpdateVersion == new Version(1, 3),
                "the first auto-updating release is 1.3");
        }
        finally
        {
            try { dlg.Close(); } catch { }
        }
    }

    private sealed class DialogLayout
    {
        public double Height;
        public double Top;
        public double[] RowTops = Array.Empty<double>();
    }

    /// <summary>Screen position of the release rows plus the dialog geometry, so a
    /// test can prove a selection never moves the rows under the cursor.</summary>
    private static DialogLayout CaptureLayout(System.Windows.Window dlg)
    {
        var layout = new DialogLayout { Height = dlg.ActualHeight, Top = dlg.Top };
        if (dlg.FindName("listReleases") is not System.Windows.Controls.ListBox list)
            return layout;

        var tops = new double[list.Items.Count];
        for (var i = 0; i < tops.Length; i++)
        {
            tops[i] = list.ItemContainerGenerator.ContainerFromIndex(i)
                is System.Windows.FrameworkElement item && item.IsVisible
                ? item.PointToScreen(new System.Windows.Point(0, 0)).Y
                : double.NaN;
        }
        layout.RowTops = tops;
        return layout;
    }

    private static void CheckLayoutStable(System.Windows.Window dlg, DialogLayout before, string what)
    {
        var after = CaptureLayout(dlg);
        Check(Math.Abs(after.Height - before.Height) < 0.5,
            $"{what} does not resize the dialog (was {before.Height:0.#}, now {after.Height:0.#})");

        var moved = new List<string>();
        for (var i = 0; i < before.RowTops.Length && i < after.RowTops.Length; i++)
        {
            if (double.IsNaN(before.RowTops[i]) || double.IsNaN(after.RowTops[i]))
                continue;
            if (Math.Abs(after.RowTops[i] - before.RowTops[i]) > 0.5)
                moved.Add($"row {i}: {before.RowTops[i]:0.#} -> {after.RowTops[i]:0.#}");
        }
        Check(moved.Count == 0,
            moved.Count == 0
                ? $"{what} leaves every release row exactly where it was"
                : $"{what} moved release rows ({string.Join(", ", moved)})");
    }

    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// Developer helper: renders the dialog with fixture releases so layout can
    /// be reviewed without opening the app or reaching GitHub.
    /// Usage: version_history_smoke --screenshot &lt;path.png&gt;
    /// </summary>
    private static int RenderScreenshot(string path)
    {
        try
        {
            _ = new System.Windows.Application();
            var dlg = new SifuMovesetEditor.Update.VersionHistoryDialog();
            dlg.ShowReleases(new List<UpdateInfo>
            {
                Make("v1.5", "1.5", 2026, 12, 4, "## What's new\n- Reworked export warnings\n- Fixed a crash on load", 42_000_000),
                Make("v1.4", "1.4", 2026, 11, 19, "## Export\n- MainChar nodes now use per-input chance pools\n- Chance sums are rebalanced only where a row was added", 41_309_233),
                Make("v1.3", "1.3", 2026, 10, 20, "- Enemy graphs keep one probability pool\n- Faster thumbnail rendering", 40_100_000),
                Make("v1.2", "1.2", 2026, 9, 12, "- Import Mod merge options", 39_500_000),
                Make("v1.1", "1.1", 2026, 8, 3, "- First signed build", 38_000_000),
            });
            dlg.SelectReleaseForTest(3);

            const double width = 620, height = 581;
            var root = (System.Windows.FrameworkElement)dlg.Content;
            root.Measure(new System.Windows.Size(width, height));
            root.Arrange(new System.Windows.Rect(0, 0, width, height));
            root.UpdateLayout();

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)width, (int)height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var stream = File.Create(full);
            encoder.Save(stream);
            Console.WriteLine($"Rendered {full}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Screenshot failed: {ex}");
            return 1;
        }
    }

    private static UpdateInfo Make(string tag, string version, int year, int month, int day,
        string notes, long size) => new()
    {
        Tag = tag,
        VersionText = version,
        Version = Version.Parse(version),
        Notes = notes,
        AssetName = "SifuMovesetEditor.zip",
        AssetUrl = "https://example/" + tag + ".zip",
        AssetSize = size,
        PublishedAt = new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero),
    };

    private static void Write(string dir, string relative, string content)
    {
        var path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Check(bool condition, string message)
    {
        if (condition)
        {
            Console.WriteLine($"  PASS {message}");
            return;
        }
        _failures++;
        Console.WriteLine($"  FAIL {message}");
    }
}
