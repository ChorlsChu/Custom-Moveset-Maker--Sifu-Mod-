using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SifuMovesetEditor;

/// <summary>
/// Interactive walkthrough runner. Lives in its own partial file so the main window code
/// stays readable: this file owns the tutorial sandbox (snapshot -> vanilla -> restore),
/// the spotlight overlay, and the few event hooks that let steps advance on their own.
/// </summary>
public partial class MainWindow
{
    private const string TutorialSnapshotFolder = "SifuMovesetEditor";

    // A tutorial session lasts as long as Help is open. The user's project is snapshotted
    // once, every scenario runs in the same vanilla sandbox, and the snapshot is restored
    // when the last Help window closes.
    private bool _tutorialSandboxActive;
    private string? _tutorialSnapshotPath;
    private bool _tutorialRestoring;

    private TutorialScenario? _tutorial;
    private int _tutorialIndex;
    private int? _tutorialFocusNodeId;
    private MoveInfo? _tutorialCardMove;
    private FrameworkElement? _tutorialCardBorder;
    private bool? _tutorialWebViewWasVisible;
    private bool _tutorialSuspended;
    private bool _tutorialPendingAdvance;
    private bool _helpOpen;

    // Completed when the current walkthrough ends (Finish, exit, Esc, or editor close). The
    // Help loop awaits this so it only reopens the guide after the overlay is gone.
    private TaskCompletionSource? _tutorialCompletion;

    private void WireTutorialOverlay()
    {
        tutorialOverlay.NextRequested += (_, _) => MoveTutorial(1);
        tutorialOverlay.BackRequested += (_, _) => MoveTutorial(-1);
        tutorialOverlay.ExitRequested += (_, _) => EndTutorialRun();
        tutorialOverlay.SizeChanged += (_, _) => tutorialOverlay.Relayout();

        // Holes track live elements, so a graph pan during a walkthrough must redraw the
        // dim/ring around whatever moved.
        _comboTranslate.Changed += (_, _) =>
        {
            if (tutorialOverlay.IsShowing) tutorialOverlay.Relayout();
        };
    }

    // ── Help <-> tutorial loop ───────────────────────────────────────────────────

    /// <summary>
    /// Opens the guide. When the user launches a walkthrough the dialog closes, the scenario
    /// runs in the sandbox, and Help reopens on the same article. The user's project comes
    /// back once, after the last Help window closes.
    /// </summary>
    private async Task OpenHelpAsync(string? topicId)
    {
        if (_opInProgress || _helpOpen) return;
        _helpOpen = true;
        try
        {
            while (true)
            {
                var dlg = new HelpDialog(topicId) { Owner = this };
                dlg.ShowDialog();

                if (dlg.PendingTutorialId == null) break;

                topicId = dlg.PendingTutorialTopicId;
                await RunTutorialAsync(dlg.PendingTutorialId);
                if (_tutorialRestoring || !IsVisible) return;
            }
        }
        finally
        {
            try { await EndTutorialSessionAsync(); }
            finally { _helpOpen = false; }
        }
    }

    private async Task RunTutorialAsync(string tutorialId)
    {
        var scenario = TutorialContent.Find(tutorialId);
        if (scenario == null || _tutorial != null || _opInProgress) return;

        if (!await BeginTutorialSandboxAsync()) return;

        // Every scenario starts from the same clean slate. The first scenario follows the
        // reset that created the sandbox, so this only costs time when the last one left edits.
        if (ProjectHasExistingChanges())
            await ResetProjectToVanillaAsync($"Tutorial: {scenario.Title}");

        await EnsureTutorialUnitAsync(scenario);

        _tutorialFocusNodeId = null;
        if (scenario.Setup == TutorialSetup.MixupNode) FocusTutorialMixupNode();

        // A drop step needs a concrete card on screen before it is shown; pick and reveal one
        // now so the library has time to lay out while step 1 is being read.
        if (scenario.Steps.Any(s => s.Focus == TutorialFocus.RandomAttackCard))
            await PrepareTutorialCardTargetAsync();

        // Runs until the overlay ends (Finish, exit, Esc, editor close). Reopening Help
        // inline would put a modal dialog over a live walkthrough, so wait here instead.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _tutorialCompletion = completion;
        try
        {
            ShowTutorialStep(scenario, 0);
            await completion.Task;
        }
        finally
        {
            if (ReferenceEquals(_tutorialCompletion, completion)) _tutorialCompletion = null;
        }
    }

    private async Task<bool> BeginTutorialSandboxAsync()
    {
        if (_tutorialSandboxActive) return true;

        if (ProjectHasExistingChanges())
        {
            SaveCurrentUnitToCache();
            string path = Path.Combine(Path.GetTempPath(), TutorialSnapshotFolder,
                $"tutorial-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.sifu-edit");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var project = new EditorProject
                {
                    Name = "tutorial-snapshot",
                    CustomMoveset = _customMoveset
                };
                var stance = _activeStance ?? "MainChar";
                var variant = _activeVariant;
                var weapon = NormalizeWeaponForCache(stance.Split('|')[0], _activeWeapon);

                await RunWithLoadingAsync("Preparing the walkthrough...", "Saving your project first",
                    async () => await Task.Run(() => ProjectManager.SaveWithCaches(
                        project, _unitCaches, stance, variant, weapon, path)));

                _tutorialSnapshotPath = path;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("TUTORIAL", ex);
                MessageBox.Show(
                    "Could not save a temporary copy of your project, so the walkthrough was not started.\n\n"
                    + ex.Message,
                    "Walkthrough", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        _tutorialSandboxActive = true;
        return true;
    }

    private async Task EndTutorialSessionAsync()
    {
        if (!_tutorialSandboxActive) return;
        _tutorialSandboxActive = false;
        await RestoreTutorialProjectAsync();
    }

    /// <summary>
    /// Puts the user's project back. Restores the snapshot when one was taken; a project that
    /// was already vanilla just gets reset, which lands on the same state.
    /// </summary>
    private async Task RestoreTutorialProjectAsync()
    {
        string? path = _tutorialSnapshotPath;
        _tutorialSnapshotPath = null;

        try
        {
            if (path != null && File.Exists(path))
            {
                bool ok = false;
                await RunWithLoadingAsync("Restoring your project...", Path.GetFileName(path), async () =>
                {
                    var result = await LoadProjectCoreAsync(path);
                    ok = result.ok;
                });

                if (!ok)
                {
                    // Load can be cancelled by the custom-file conflict prompt. Keep the
                    // snapshot so the work isn't lost and point the user at it.
                    _tutorialSnapshotPath = path;
                    txtStatus.Text = $"Project restore cancelled — your work is still saved at {path}";
                    return;
                }

                txtStatus.Text = "Project restored — back to where you were";
            }
            else
            {
                await ResetProjectToVanillaAsync("Walkthrough finished");
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("TUTORIAL", ex);
            if (path != null) _tutorialSnapshotPath = path;
            MessageBox.Show(
                "The walkthrough could not put your project back automatically.\n\n"
                + (path != null ? $"Your saved copy is at:\n{path}\n\n" : "")
                + ex.Message,
                "Walkthrough", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            if (path != null && _tutorialSnapshotPath == null)
            {
                try { File.Delete(path); } catch { }
            }
        }
    }

    /// <summary>Restore, then close. Used when the window is closed mid-walkthrough.</summary>
    private async Task RestoreTutorialAndCloseAsync()
    {
        _tutorialRestoring = true;
        _tutorialSuspended = false;
        _tutorialPendingAdvance = false;
        // Release the Help loop; the guard after RunTutorialAsync keeps it from reopening.
        _tutorialCompletion?.TrySetResult();
        try
        {
            tutorialOverlay.HideOverlay();
            _tutorial = null;
            if (_tutorialWebViewWasVisible is bool was)
            {
                _tutorialWebViewWasVisible = null;
                SetWebViewVisible(was);
            }
            _tutorialSandboxActive = false;
            await RestoreTutorialProjectAsync();
        }
        finally
        {
            _tutorialRestoring = false;
        }
        Close();
    }

    // ── Unit prep ───────────────────────────────────────────────────────────────

    private async Task EnsureTutorialUnitAsync(TutorialScenario scenario)
    {
        string arch = _activeStance?.Split('|')[0] ?? "";
        if (!string.Equals(arch, scenario.EntryStance, StringComparison.OrdinalIgnoreCase))
            await SwitchStanceAsync(scenario.EntryStance);

        if (scenario.EntryVariant != null
            && !string.Equals(_activeVariant, scenario.EntryVariant, StringComparison.OrdinalIgnoreCase))
            await LoadVariantComboGraphAsync(scenario.EntryVariant, saveCurrent: false);
    }

    /// <summary>
    /// Picks the vanilla AttackDB node with the strongest real chance pool for the
    /// Conditions walkthrough. Ranked by transition rows for the node's resolved write
    /// struct (not by graph edges: one conditional row can fan out to several targets),
    /// then by conditioned rows, then edges. Authored/imported/action/event/redirect
    /// nodes are excluded because the dialog edits vanilla struct rows.
    /// </summary>
    private void FocusTutorialMixupNode()
    {
        _tutorialFocusNodeId = null;
        if (_comboGraph == null) return;

        // Old saves carry no TreeRows snapshot; parse it here so ranking and the dialog
        // both see the vanilla rows.
        if (_comboGraph.TreeRows.Count == 0 && !_comboGraph.TreeRowsBackfillAttempted
            && !string.IsNullOrEmpty(_comboGraph.SourceGamePath)
            && _comboGraph.Nodes.Any(n => n.TreeIndex >= 0))
            BackfillTreeRows();

        var ranked = _comboGraph.Nodes
            .Where(n => !n.Removed && !n.IsRoot && !n.IsRedirect && !n.IsActionNode
                && !n.IsEventNode && n.TreeIndex >= 0
                && n.Kind == ComboNodeKind.Vanilla && !n.IsImportedFromMod)
            .Select(n =>
            {
                int structIdx = TreeChanceMath.ResolveWriteStruct(_comboGraph, n);
                var rows = structIdx >= 0 && _comboGraph.TreeRows.TryGetValue(structIdx, out var rr)
                    ? rr
                    : new List<TreeRowInfo>();
                int edges = _comboGraph.Edges.Count(e => e.FromNodeId == n.Id && !e.IsRedirect);
                return (node: n, rows: rows.Count,
                    conditioned: rows.Count(r => r.ConditionPresent), edges: edges);
            })
            .Where(x => x.rows >= 2 || x.edges >= 2)
            .OrderByDescending(x => x.rows)
            .ThenByDescending(x => x.conditioned)
            .ThenByDescending(x => x.edges)
            .ThenBy(x => x.node.Depth)
            .ThenBy(x => x.node.Id)
            .ToList();

        if (ranked.Count == 0) return;

        // Prefer a branch with real chance rows, but never leave the step without a spotlight.
        var withData = ranked.FirstOrDefault(x => HasBranchChanceData(x.node));
        var chosen = withData.node ?? ranked[0].node;
        _tutorialFocusNodeId = chosen.Id;
    }

    // ── Card picker ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Picks a random vanilla AttackDB card, expands its library groups, scrolls it into view
    /// and highlights it. Step 2 of Replace a move spotlights this exact card; if anything
    /// fails the anchor falls back to the whole library, which still allows the drop.
    /// </summary>
    private async Task PrepareTutorialCardTargetAsync()
    {
        _tutorialCardMove = null;
        _tutorialCardBorder = null;

        if (_allMoves.Count == 0) return;

        if (_aiLibraryMode) SetLibraryMode(false);
        if (!string.IsNullOrEmpty(txtSearch.Text))
        {
            txtSearch.Text = "";
            FilterMoves();
        }

        tabMoves.SelectedIndex = TabVanilla;

        var candidates = _allMoves
            .Where(m => m.IsUsed && m.IsValid && !IsOldCard(m)
                && IsAttackDbPath(m.FullPath ?? ""))
            .ToList();
        if (candidates.Count == 0) return;

        var move = candidates[Random.Shared.Next(candidates.Count)];
        _tutorialCardMove = move;

        ExpandLibraryGroupsFor(move);
        FilterMoves();
        listVanilla.UpdateLayout();
        await Task.Delay(60);

        var border = FindVisualChild<Border>(listVanilla,
            b => b.Tag is MoveInfo mi && !string.IsNullOrEmpty(mi.FullPath)
                && string.Equals(mi.FullPath, move.FullPath, StringComparison.OrdinalIgnoreCase));
        if (border == null) return;

        _tutorialCardBorder = border;
        ScrollToElement(border);
        HighlightCard(border);
        PopCard(border);
    }

    private FrameworkElement? ResolveTutorialCardBorder()
    {
        if (_tutorialCardBorder is { IsVisible: true, ActualWidth: > 1 }) return _tutorialCardBorder;

        var move = _tutorialCardMove;
        if (move == null) return null;

        _tutorialCardBorder = FindVisualChild<Border>(listVanilla,
            b => b.Tag is MoveInfo mi && !string.IsNullOrEmpty(mi.FullPath)
                && string.Equals(mi.FullPath, move.FullPath, StringComparison.OrdinalIgnoreCase));
        return _tutorialCardBorder;
    }

    /// <summary>Expands the accordion groups that hold <paramref name="move"/>.</summary>
    private void ExpandLibraryGroupsFor(MoveInfo move)
    {
        if (string.IsNullOrEmpty(move.Character)) return;
        _expandedEnemies.Add(move.Character);
        if (!_expandedWeapons.TryGetValue(move.Character, out var weapons))
        {
            weapons = new HashSet<string>();
            _expandedWeapons[move.Character] = weapons;
        }

        if (string.Equals(move.Character, "MainChar", StringComparison.OrdinalIgnoreCase))
        {
            if (IsSpecialComboDb(move)) { weapons.Add("Special Combos"); return; }
            if (IsSpecialDb(move)) { weapons.Add("Special"); return; }

            // The tree group label can differ from the card's WeaponType (Bats -> Bat).
            if (!string.IsNullOrEmpty(move.FullPath))
            {
                foreach (var kv in _mainCharWeaponMoves)
                {
                    if (kv.Value.Any(m => !string.IsNullOrEmpty(m.FullPath)
                            && m.FullPath.Equals(move.FullPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        weapons.Add(kv.Key);
                        return;
                    }
                }
            }

            var label = move.WeaponType switch
            {
                "Bats" => "Bat",
                "Blades" => "Knife",
                _ => move.WeaponType
            };
            if (!string.IsNullOrEmpty(label)) weapons.Add(label);
            return;
        }

        if (!string.IsNullOrEmpty(move.WeaponType)) weapons.Add(move.WeaponType);
    }

    /// <summary>One short scale pop so the eye finds the card before the dim lands.</summary>
    private static void PopCard(FrameworkElement card)
    {
        var scale = new ScaleTransform(1, 1);
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        card.RenderTransform = scale;

        var pop = new DoubleAnimationUsingKeyFrames();
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.07,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110))));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))));
        pop.Completed += (_, _) => card.RenderTransform = Transform.Identity;

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>
    /// Pans the graph when the spotlighted node is outside the viewport, so a ring is never
    /// pointing at something the user cannot see or click. Mirrors the pan used by the
    /// redirect jump: only acts when the node doesn't fit.
    /// </summary>
    private void EnsureTutorialNodeVisible(int nodeId)
    {
        if (!_nodePositions.TryGetValue(nodeId, out var pos)) return;
        if (comboBorder.ActualWidth < 1 || comboBorder.ActualHeight < 1) return;

        const double margin = 28;
        double viewL = -_comboTranslate.X, viewT = -_comboTranslate.Y;
        double viewR = viewL + comboBorder.ActualWidth, viewB = viewT + comboBorder.ActualHeight;

        bool visible = pos.X >= viewL + margin
                       && pos.X + NODE_WIDTH <= viewR - margin
                       && pos.Y >= viewT + margin
                       && pos.Y + NODE_HEIGHT <= viewB - margin;
        if (visible) return;

        _comboTranslate.X = comboBorder.ActualWidth / 2.0 - (pos.X + NODE_WIDTH / 2.0);
        _comboTranslate.Y = comboBorder.ActualHeight / 2.0 - (pos.Y + NODE_HEIGHT / 2.0);
    }

    // ── Overlay driving ─────────────────────────────────────────────────────────

    private void ShowTutorialStep(TutorialScenario scenario, int index)
    {
        if (index < 0 || index >= scenario.Steps.Count)
        {
            EndTutorialRun();
            return;
        }

        _tutorial = scenario;
        _tutorialIndex = index;

        // WebView2 is an airspace control: it renders above WPF, so the dim can't cover it.
        // Hide the 3D viewer for the duration of the walkthrough instead.
        if (_tutorialWebViewWasVisible == null)
        {
            // A preview that was still loading has already hidden the viewer, but the user
            // expects it back after Help closes - treat it as visible.
            bool previewLoading = _previewPopup?.IsOpen == true;
            _tutorialWebViewWasVisible = previewLoading || webView.Visibility == Visibility.Visible;

            // The loading card is a top-level popup, so it would sit above the step bubble.
            // Invalidate the load too: its completion must not touch the viewer now.
            _previewGen++;
            if (_previewPopup != null) _previewPopup.IsOpen = false;

            if (_tutorialWebViewWasVisible == true) SetWebViewVisible(false);
        }

        var step = scenario.Steps[index];
        tutorialOverlay.Visibility = Visibility.Visible;

        // A walkthrough must never ring something the user can't see: pan the graph when
        // the spotlighted node sits outside the viewport.
        if (_tutorialFocusNodeId is int focusId) EnsureTutorialNodeVisible(focusId);

        tutorialOverlay.UpdateLayout();

        var targets = ResolveTutorialAnchor(step.Anchor);
        var ring = ResolveTutorialRing(step, targets);
        tutorialOverlay.ShowStep(scenario, index, targets, ring);
        txtStatus.Text = $"Walkthrough — {scenario.Title} ({index + 1}/{scenario.Steps.Count})";
    }

    private void MoveTutorial(int delta)
    {
        if (_tutorial == null) return;

        int next = _tutorialIndex + delta;
        if (next >= _tutorial.Steps.Count)
        {
            EndTutorialRun();
            return;
        }
        ShowTutorialStep(_tutorial, Math.Max(0, next));
    }

    private void EndTutorialRun()
    {
        tutorialOverlay.HideOverlay();
        _tutorial = null;
        _tutorialIndex = 0;
        _tutorialFocusNodeId = null;
        _tutorialCardMove = null;
        _tutorialCardBorder = null;
        _tutorialSuspended = false;
        _tutorialPendingAdvance = false;

        if (_tutorialWebViewWasVisible is bool was)
        {
            _tutorialWebViewWasVisible = null;
            SetWebViewVisible(was);
        }

        txtStatus.Text = "Walkthrough finished — reopening help";

        // Queued on the dispatcher, so the click that ended the walkthrough finishes
        // routing before a new Help dialog opens.
        _tutorialCompletion?.TrySetResult();
    }

    /// <summary>
    /// Auto-advance hook. Called from editor actions; a no-op unless the current step is
    /// waiting for exactly this event.
    /// </summary>
    private void TutorialEvent(string eventId, int? focusNodeId = null)
    {
        if (_tutorial == null || !tutorialOverlay.IsShowing) return;

        var step = _tutorial.Steps[_tutorialIndex];
        if (step.Advance != TutorialAdvance.Event || step.EventId != eventId) return;

        if (focusNodeId is int id) _tutorialFocusNodeId = id;

        // Let the action that raised the event finish painting before moving on, so the next
        // step can spotlight whatever it just created.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_tutorial == null) return;
            if (_tutorialSuspended)
            {
                // A modal (Conditions & Chances) swallowed the change; advance when it closes.
                _tutorialPendingAdvance = true;
                return;
            }
            if (tutorialOverlay.IsShowing) MoveTutorial(1);
        }), DispatcherPriority.Background);
    }

    /// <summary>True when the current step asks for a drop on a still-vanilla node.</summary>
    private bool TutorialWantsVanillaDrop
        => _tutorial != null
           && _tutorialIndex >= 0
           && _tutorialIndex < _tutorial.Steps.Count
           && _tutorial.Steps[_tutorialIndex].Requirement == TutorialRequirement.VanillaNode;

    /// <summary>
    /// Gate for card drops while a walkthrough is up. The check runs before the drop is
    /// applied, because replacing the node would immediately make it non-vanilla.
    /// </summary>
    private bool TutorialAllowsCardDrop(int nodeId)
    {
        if (!TutorialWantsVanillaDrop) return true;
        if (IsVanillaTutorialNode(nodeId)) return true;

        var node = _comboGraph?.Nodes.FirstOrDefault(n => n.Id == nodeId);
        tutorialOverlay.ShowNudge(node is { IsRoot: true }
            ? "That's the Start node — drop the card on a blue node instead."
            : "That node is already edited — drop the card on a blue node.");
        return false;
    }

    /// <summary>True when the node still shows its vanilla move (blue in the graph).</summary>
    private bool IsVanillaTutorialNode(int nodeId)
    {
        var node = _comboGraph?.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null || node.Removed) return false;
        if (node.IsRedirect || node.IsActionNode || node.IsEventNode || node.IsRoot) return false;
        if (node.Kind is ComboNodeKind.UserCreated or ComboNodeKind.ImportedNew) return false;
        if (node.IsImportedFromMod || node.ReplacedByAiNode) return false;

        // Same rules GetNodeColor uses: a different vanilla path or default path is edited.
        if (!string.IsNullOrEmpty(node.VanillaAnimPath)
            && !string.Equals(node.AnimPath, node.VanillaAnimPath, StringComparison.Ordinal))
            return false;
        if (!string.Equals(node.AnimPath, node.DefaultAnimPath, StringComparison.Ordinal))
            return false;

        // A different source DB counts as edited even when the animation path matches.
        return string.IsNullOrEmpty(node.DefaultDBPath)
            || string.IsNullOrEmpty(node.SourceDBPath)
            || string.Equals(node.SourceDBPath, node.DefaultDBPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the step currently on screen is waiting for this exact event.</summary>
    private bool TutorialStepExpects(string eventId)
        => _tutorial != null
           && _tutorialIndex >= 0
           && _tutorialIndex < _tutorial.Steps.Count
           && _tutorial.Steps[_tutorialIndex].EventId == eventId;

    /// <summary>
    /// Node context menus are needed to start a link or open Conditions & Chances. Every
    /// other walkthrough step swallows them so Clone/Delete can't rewrite the sandbox.
    /// </summary>
    private bool TutorialAllowsNodeMenu
        => _tutorial != null
           && _tutorialIndex >= 0
           && _tutorialIndex < _tutorial.Steps.Count
           && _tutorial.Steps[_tutorialIndex].EventId
               is TutorialEvents.LinkStarted or TutorialEvents.ChancesClosed;

    private FrameworkElement[] ResolveTutorialAnchor(string? key)
    {
        switch (key)
        {
            case TutorialAnchors.Library: return new FrameworkElement[] { libraryPanel };
            case TutorialAnchors.LibraryTabs: return new FrameworkElement[] { tabMoves };
            case TutorialAnchors.Search: return new FrameworkElement[] { txtSearch };
            case TutorialAnchors.Toolbar: return new FrameworkElement[] { comboToolbar };
            case TutorialAnchors.ChangeUnit: return new FrameworkElement[] { btnChangeUnit };
            case TutorialAnchors.StartEntries: return new FrameworkElement[] { btnStartEntries };
            case TutorialAnchors.LibraryAndGraph:
                return new FrameworkElement[] { libraryPanel, comboBorder };
            case TutorialAnchors.LibraryCard:
                return new FrameworkElement[]
                {
                    ResolveTutorialCardBorder() ?? libraryPanel,
                    comboBorder
                };
            case TutorialAnchors.LastTarget:
                return new FrameworkElement[]
                {
                    _tutorialFocusNodeId is int id && _nodeBorders.TryGetValue(id, out var border)
                        ? border
                        : comboBorder
                };
            case TutorialAnchors.Graph:
            default:
                return new FrameworkElement[] { comboBorder };
        }
    }

    /// <summary>
    /// The one control the ring points at. Card steps ring the card (the graph is a hole but
    /// not the focus); a single concrete control rings itself. Whole panels (library, graph)
    /// only get the hole — a ring around them would just outline the dim edge.
    /// </summary>
    private FrameworkElement? ResolveTutorialRing(TutorialStep step, IReadOnlyList<FrameworkElement> targets)
    {
        if (step.Anchor == TutorialAnchors.LibraryCard)
            return ResolveTutorialCardBorder();

        return targets.Count == 1
               && step.Anchor != TutorialAnchors.Library
               && !ReferenceEquals(targets[0], comboBorder)
            ? targets[0]
            : null;
    }

    // ── Modal suspension ────────────────────────────────────────────────────────

    /// <summary>
    /// Hides the overlay while a modal dialog is up so it can't sit dimmed behind it.
    /// Returns whether it was showing, for <see cref="ResumeTutorialAfterDialog"/>.
    /// </summary>
    private bool SuspendTutorialForDialog()
    {
        if (!tutorialOverlay.IsShowing) return false;
        _tutorialSuspended = true;
        tutorialOverlay.Visibility = Visibility.Collapsed;
        return true;
    }

    private void ResumeTutorialAfterDialog(bool wasShowing)
    {
        if (!wasShowing) return;
        _tutorialSuspended = false;
        if (_tutorial == null) return;

        tutorialOverlay.Visibility = Visibility.Visible;
        tutorialOverlay.UpdateLayout();
        tutorialOverlay.Relayout();

        if (_tutorialPendingAdvance)
        {
            _tutorialPendingAdvance = false;
            MoveTutorial(1);
            return;
        }

        if (_tutorial.Steps[_tutorialIndex].EventId == TutorialEvents.ChancesClosed)
            TutorialEvent(TutorialEvents.ChancesClosed);
    }
}
