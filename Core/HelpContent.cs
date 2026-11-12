using System;
using System.Collections.Generic;
using System.Linq;

namespace SifuMovesetEditor;

/// <summary>
/// The feature guide shown by <see cref="HelpDialog"/>. Content lives in code (like
/// UnitPropsHelp) so it ships with the app, needs no external files, and can be
/// searched without parsing. Topics keep authoring order; categories are assembled
/// from <see cref="CategoryOrder"/> so the sidebar reads like a learning path.
/// </summary>
public static class HelpContent
{
    private static readonly string[] CategoryOrder =
    {
        "Getting Started",
        "Nodes & Links",
        "MainChar vs Enemies",
        "Chances & Conditions",
        "Library & Cards",
        "Units & Export",
        "Troubleshooting"
    };

    /// <summary>Every guide, grouped by category in sidebar order.</summary>
    public static IReadOnlyList<HelpCategory> Categories { get; }

    /// <summary>Flat list, used by search and the smoke test.</summary>
    public static IReadOnlyList<HelpTopic> Topics { get; }

    static HelpContent()
    {
        Topics = BuildTopics();
        Categories = CategoryOrder
            .Select(name => new HelpCategory(name,
                Topics.Where(t => string.Equals(t.Category, name, StringComparison.Ordinal)).ToArray()))
            .Where(c => c.Topics.Count > 0)
            .ToArray();
    }

    /// <summary>
    /// Case-insensitive multi-term search: every whitespace-separated term must appear
    /// somewhere in the topic (title, summary, steps, tips, keywords or location).
    /// An empty query returns every topic.
    /// </summary>
    public static IReadOnlyList<HelpTopic> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Topics;

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return Topics;

        return Topics.Where(t => terms.All(t.Matches)).ToArray();
    }

    public static HelpTopic? Find(string id)
        => Topics.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    private static List<HelpTopic> BuildTopics() => new()
    {
        // ── Getting Started ─────────────────────────────────────────────────────
        T("projects", "Getting Started",
            "Open, save & import projects",
            "A project stores every edited unit, custom node and imported mod in one .sifu-edit file.",
            "Top bar: New, Import Mod, Save, Load.",
            new[]
            {
                "New resets the current session to vanilla. Custom library files stay on disk.",
                "Save writes a .sifu-edit project and asks where to put it.",
                "Load restores a saved project, including per-unit graphs, custom nodes and cached edits.",
                "Import Mod pulls content from an existing .pak into the current project; choose fresh or merge when asked."
            },
            null,
            new[] { "project", "save", "load", "open", "import", "sifu-edit" }),

        T("undo-redo", "Getting Started",
            "Undo & redo",
            "Graph edits are undoable per unit, so switching movesets does not lose your history.",
            "Keyboard: Ctrl+Z and Ctrl+Shift+Z.",
            new[]
            {
                "Press Ctrl+Z to undo the last graph edit on the current unit.",
                "Press Ctrl+Shift+Z to redo it.",
                "Switch units freely: each unit keeps its own undo and redo stack.",
                "Project-wide operations such as Import Mod, New Project and custom card delete/rename clear the history."
            },
            new[]
            {
                "An action that changes several rows at once, such as Even split, counts as one undo step.",
                "If a dialog is open, finish or cancel it before using undo."
            },
            new[] { "undo", "redo", "history", "ctrl+z" }),

        // ── Nodes & Links ───────────────────────────────────────────────────────
        T("replace-move", "Nodes & Links",
            "Replace a move (drag & drop)",
            "Drag a card from the library onto a blue vanilla node to swap that move for another.",
            "Library on the right → combo graph on the left.",
            new[]
            {
                "Pick an attack in Library → Attacks. The Vanilla, Animations and Custom groups hold different card sources.",
                "Drag the card onto a blue node on the combo graph.",
                "The node turns green and shows the new move. Double-click it to preview the animation in the 3D viewer.",
                "Dropped it on the wrong node? Press Ctrl+Z or drag another card onto it."
            },
            new[]
            {
                "Dropping a card on empty canvas creates a new node instead of replacing one.",
                "A green node is a replaced move; blue nodes are still vanilla."
            },
            new[] { "replace", "drag", "drop", "swap", "move", "node", "library" },
            tutorialId: "replace-move"),

        T("add-move", "Nodes & Links",
            "Add a new move to the graph",
            "Drop a library card on empty canvas to create a node, then link it into an existing route.",
            "Library → empty space on the combo graph.",
            new[]
            {
                "Find the card you want in Library → Attacks.",
                "Drag it onto empty canvas space. A new node appears at the drop point.",
                "Right-click the new node and choose Connect to. to link it from an existing move.",
                "For enemy actions outside the attack tables (dashes, throws, events), enable Show AI Actions & Events in Settings and use the AI Actions & Events library tab."
            },
            new[]
            {
                "A floating node is not part of the combo yet. Link it from a route that already reaches the tree.",
                "Custom nodes and their links are saved in the project and exported with it."
            },
            new[] { "add", "new", "node", "create", "custom", "drag", "canvas", "ai" }),

        T("link-moves", "Nodes & Links",
            "Link two moves",
            "Connect any node to any node, then choose the input that should trigger the follow-up.",
            "Right-click the source node → Connect to.",
            new[]
            {
                "Right-click the source node and choose Connect to.",
                "Click the target node on the graph.",
                "Pick an input in the popup. MainChar offers player inputs; enemy graphs offer Default (None) because their links are AI-gated.",
                "If the source's typed chances cannot stay at 100%, Conditions & Chances opens so you can review the pool."
            },
            new[]
            {
                "Redirects and removed nodes cannot be link targets.",
                "The same source → target → input combination can only exist once."
            },
            new[] { "link", "connect", "transition", "edge", "input" }),

        T("redirect-links", "Nodes & Links",
            "Redirect links",
            "A redirect is a small helper node that forwards to another move without duplicating the route.",
            "Right-click a node → Add Redirect via new node.",
            new[]
            {
                "Right-click the source node and choose Add Redirect via new node.",
                "Click the node the redirect should forward to.",
                "A redirect node appears between them; the link from the source still carries the input.",
                "Right-click the redirect and use the redirect highlight to see where it forwards."
            },
            new[]
            {
                "Redirects are routing, not chance branches. Removing one does not change a pool total.",
                "Deleting an authored destination also removes the redirects that pointed at it."
            },
            new[] { "redirect", "forward", "route", "helper", "node" }),

        T("delete-nodes", "Nodes & Links",
            "Delete & restore nodes",
            "Authored nodes are removed immediately; vanilla nodes are crossed out and dropped on export.",
            "Right-click a node → Delete node / Restore Node.",
            new[]
            {
                "Right-click the node and choose Delete node.",
                "A vanilla node is marked removed and struck through. Use Restore Node on the same menu to bring it back.",
                "A custom or AI node is removed immediately with every link that touched it.",
                "If typed chances cannot rebalance after the delete, Conditions & Chances opens for the affected source."
            },
            new[]
            {
                "Undo (Ctrl+Z) brings a hard-deleted node back, including its links.",
                "Vanilla removals are pruned and rebalanced at export, so they can only show a status hint at delete time."
            },
            new[] { "delete", "remove", "restore", "node", "vanilla", "undo" }),

        T("delete-link", "Nodes & Links",
            "Delete a link",
            "Remove a single transition without deleting either node.",
            "Right-click the link on the graph → Delete Link.",
            new[]
            {
                "Right-click the line you want to remove.",
                "Choose Delete Link. The two nodes stay in place.",
                "If the link owned a standalone chance row, the source's pool is re-checked.",
                "The delete is committed immediately; use Ctrl+Z to undo it."
            },
            new[]
            {
                "Deleting a merged link turns its row back into the link's own appended row.",
                "Deleting a redirect link removes the routing hop, not the destination."
            },
            new[] { "delete", "remove", "link", "edge", "transition" }),

        // ── MainChar vs Enemies ─────────────────────────────────────────────────
        T("mainchar-combos", "MainChar vs Enemies",
            "MainChar combos & inputs",
            "Player links choose a real input, and each input keeps its own 100% chance pool.",
            "Change Unit → MainChar, then right-click a move → Connect to.",
            new[]
            {
                "Use Change Unit to select MainChar and the stance or weapon variant you want to edit.",
                "Right-click a move and choose Connect to., then click the follow-up move.",
                "Pick the input that should trigger it: Light, Heavy, Rush, Dodge, and so on. Custom input. accepts a raw EComboTransition name.",
                "When several links share an input, their chances must total 100% for that input."
            },
            new[]
            {
                "Light 100% plus Heavy 100% on the same node is valid: those are separate pools.",
                "The post-link check only opens Conditions & Chances when typed values leave a pool off 100%."
            },
            new[] { "mainchar", "player", "input", "light", "heavy", "rush", "link", "combo" },
            tutorialId: "link-mainchar"),

        T("enemy-combos", "MainChar vs Enemies",
            "Enemy combos (AI-gated links)",
            "Enemy transitions do not use player inputs; their links are chosen by the AI.",
            "Change Unit → enemy/boss → right-click a move → Connect to.",
            new[]
            {
                "Use Change Unit to pick the enemy or boss, plus a variant or weapon if it has one.",
                "Right-click a move and choose Connect to., then click the follow-up.",
                "Pick Default (None) in the input popup. Enemy links are AI-gated by design.",
                "Tune which branch is offered and how often in Conditions & Chances."
            },
            new[]
            {
                "AI Action and Event nodes live outside the attack tables. Enable Show AI Actions & Events in Settings to see and add them.",
                "Enemy nodes use one chance pool per entry or ticket instead of one per input."
            },
            new[] { "enemy", "boss", "ai", "link", "none", "combo", "ticket" },
            tutorialId: "link-enemy"),

        T("per-input-pools", "MainChar vs Enemies",
            "Per-input chance pools",
            "Player nodes roll a separate chance set for each input, so each input is its own 100% pool.",
            "Conditions & Chances on a MainChar node.",
            new[]
            {
                "Select a MainChar node with several outgoing links.",
                "Open Conditions & Chances from its right-click menu.",
                "The header shows every pool at a glance, for example LMB 100% · RMB 100%.",
                "Use Σ→100% or Even split within one input to rebalance only that pool."
            },
            new[]
            {
                "The save and export warnings only flag input pools you actually edited.",
                "Enemy nodes use one pool per entry/ticket, not per input."
            },
            new[] { "chance", "pool", "per input", "mainchar", "light", "heavy", "100" }),

        T("start-entries", "MainChar vs Enemies",
            "Start Entries",
            "Start Entries decide which moves a combo can begin from, with their own chance and condition sets.",
            "Combo toolbar → Start Entries.",
            new[]
            {
                "Click Start Entries above the combo graph.",
                "Each row is a start state such as Idle, Dodge or Parry, with its own transition pool.",
                "Add a transition, retarget its outcome and set its probability or condition.",
                "Use Even split when several transitions should share the pool equally."
            },
            new[]
            {
                "Conditional rows keep their per-key outcomes: 0 is usually True and 1 is False.",
                "Unconditional fallback rows stay unconditional when you add transitions."
            },
            new[] { "start", "entry", "entries", "idle", "dodge", "parry", "ticket", "chance" }),

        // ── Chances & Conditions ────────────────────────────────────────────────
        T("chances-overview", "Chances & Conditions",
            "Conditions & Chances overview",
            "Conditions gate whether a follow-up is offered; chances decide how often it is picked when it is.",
            "Right-click a node → Conditions & Chances.",
            new[]
            {
                "Right-click the source node and open Conditions & Chances.",
                "Each outgoing route has a probability row. Automatic rows share whatever the typed rows leave.",
                "Add or edit a condition on a row to gate when that route can be chosen.",
                "Close with OK to apply the edits to the graph."
            },
            new[]
            {
                "A pool must total 100%. The dialog's header shows each pool and warns when one is off.",
                "The editor can open this dialog automatically after a link or delete when typed values no longer reach 100%."
            },
            new[] { "conditions", "chances", "mixup", "probability", "pool", "dialog" },
            tutorialId: "conditions"),

        T("chance-editing", "Chances & Conditions",
            "Exact values, Σ→100% & Even split",
            "Type exact probabilities, scale a pool to 100%, or split it evenly.",
            "Conditions & Chances → probability fields and Σ→100% / Even split buttons.",
            new[]
            {
                "Type a number in a row to make it explicit. Leave the field blank to keep the row automatic.",
                "Σ→100% scales the displayed rows proportionally so the pool totals exactly 100%.",
                "Even split spreads the pool evenly across the rows that share it.",
                "Automatic rows absorb the remainder left by typed values."
            },
            new[]
            {
                "Clearing a typed value returns that row to automatic.",
                "The editor never silently rewrites a value you typed; it opens the dialog instead."
            },
            new[] { "exact", "probability", "percent", "100", "even split", "normalize", "automatic" }),

        T("post-link-delete", "Chances & Conditions",
            "Post-link & post-delete checks",
            "The editor re-checks a source's chance pool after linking or deleting, and opens the dialog when it cannot reach 100%.",
            "Automatic after Connect to., Delete node and Delete Link.",
            new[]
            {
                "Add a link: the source's outgoing pool is evaluated immediately.",
                "Delete a node or link: every affected source is re-checked after the edges are removed.",
                "Automatic rows absorb the change silently. Typed values that block 100% open Conditions & Chances.",
                "If several moves are affected, their dialogs open one after another; Cancel stops the queue and keeps the delete."
            },
            new[]
            {
                "Deleting a vanilla node only shows a status hint, because its row is pruned and rebalanced at export.",
                "Closing a dialog without changing anything is safe: the link or delete stays, and you can revisit it later."
            },
            new[] { "post", "link", "delete", "check", "chance", "warning", "auto" }),

        T("add-condition", "Chances & Conditions",
            "Add a condition",
            "Conditions attach to a single route and decide whether it is eligible to roll.",
            "Conditions & Chances → condition column on a row.",
            new[]
            {
                "Open Conditions & Chances for the source node.",
                "Pick the route you want to gate and add or choose a condition class.",
                "Fill in its parameters or keys. For conditional outcome maps, key 0 is usually True and key 1 is False.",
                "Press OK to apply the condition to that route."
            },
            new[]
            {
                "Keep an unconditional fallback route if the move must always continue.",
                "A condition that never evaluates true makes its branch dead; the post-link check can reveal that."
            },
            new[] { "condition", "gate", "require", "true", "false", "key", "route" }),

        // ── Library & Cards ─────────────────────────────────────────────────────
        T("attackdb-cards", "Library & Cards",
            "Create, clone, rename & delete AttackDB cards",
            "Cards are the move definitions you drag onto nodes. Card management lives in the library right-click menu.",
            "Library → right-click an attack card.",
            new[]
            {
                "Clone attack card. copies an existing card; tick Clone hit window to give the copy private hit data.",
                "Create AttackDB. turns an animation into a usable card from a chosen template.",
                "Rename AttackDB. performs a real file and object rename and repoints every graph that uses the card.",
                "Delete AttackDB. removes the card permanently and warns about every affected slot and route."
            },
            new[]
            {
                "Custom cards live in the persistent Custom library and are included in saved projects and exports.",
                "Deleting or renaming a card clears undo history because it can span several cached units."
            },
            new[] { "attackdb", "card", "clone", "create", "rename", "delete", "library", "custom" }),

        T("custom-inputs", "Library & Cards",
            "Custom inputs",
            "Use a raw EComboTransition name when a link or start entry needs an input the list does not offer.",
            "Link input picker or Start Entries → Custom input.",
            new[]
            {
                "In the link input picker, choose Custom input.",
                "Type the raw EComboTransition name, for example Special or Dodge.",
                "Only names the game actually declares have gameplay meaning; anything else is written verbatim.",
                "The same option appears in the Start Entries input dropdown."
            },
            new[]
            {
                "Double-check the spelling against the game's enum; a typo exports without an error.",
                "Custom inputs are for advanced routing, not for renaming an existing vanilla input."
            },
            new[] { "custom", "input", "ecombotransition", "raw", "special", "dodge" }),

        T("hit-windows", "Library & Cards",
            "Hit windows",
            "Edit when a hit connects and what it does per hit, without touching other cards that share the row.",
            "Select an attack node → Advanced hit windows...",
            new[]
            {
                "Select an attack node and click Advanced hit windows... in the node panel.",
                "Adjust the connect window and per-hit fields: parriable, damage, structure, stun, dizzy, freeze and more.",
                "Use Make private to give this card its own copy of the hit row instead of sharing the vanilla one.",
                "Save the dialog; only modified cards are written on export."
            },
            new[]
            {
                "A shared-row warning means the edit would affect other cards using the same hit data.",
                "Private hit windows are stored with the card and travel with it when cloned or renamed."
            },
            new[] { "hit", "window", "damage", "parry", "stun", "structure", "private", "hitbox" }),

        // ── Units & Export ──────────────────────────────────────────────────────
        T("change-unit", "Units & Export",
            "Change unit, variant & weapon",
            "Every unit keeps its own graph, edits and undo history.",
            "Combo toolbar → Change Unit (then Variant / Weapon when available).",
            new[]
            {
                "Click Change Unit above the combo graph.",
                "Pick the character: the player, an enemy, or a boss.",
                "Use Variant for phases and Weapon for weapon-specific movesets when the buttons appear.",
                "Switch back at any time; cached edits and per-unit undo history are preserved."
            },
            new[]
            {
                "MainChar is the default unit on startup.",
                "Enemy graphs do not offer player inputs; their links use Default (None)."
            },
            new[] { "unit", "character", "variant", "weapon", "change", "stance", "phase" }),

        T("unit-properties", "Units & Export",
            "Unit Properties",
            "Tune health, structure and AI defense behavior for enemies and bosses.",
            "Combo toolbar → Unit Properties (non-MainChar units).",
            new[]
            {
                "Select an enemy or boss. The Unit Properties button appears for non-MainChar units.",
                "Edit Health, Structure and the AI defense fields: Memory Limit, Hits Count and Flush Limit.",
                "Optionally set a Model override, Phase Swap, or Immune to Focus.",
                "Hover the small ? next to a field for a detailed explanation of its range and side effects."
            },
            new[]
            {
                "Immune to Focus and model overrides can apply to a whole family; the ? popup names every affected variant.",
                "Unit Properties changes are written on export and are included in saved projects."
            },
            new[] { "unit properties", "health", "structure", "ai", "defense", "phase swap", "model", "focus" }),

        T("export", "Units & Export",
            "Export a Pak",
            "Export writes only the files your edits touched, plus any custom assets, into a mod pak.",
            "Top bar → Export Pak.",
            new[]
            {
                "Click Export Pak. Set the output folder in Settings if you have not already.",
                "Review the warnings: chance pools, dead branches, missing cards and shared hit rows.",
                "Export builds a .pak and .sig pair. The log lists every patched or skipped file.",
                "Copy both files into Sifu/Content/Paks/~mods/ and launch the game."
            },
            new[]
            {
                "Untouched vanilla data is never rewritten, so a small mod produces a small pak.",
                "If the review shows an unexpected change, cancel and fix it before exporting."
            },
            new[] { "export", "pak", "sig", "mod", "build", "output", "warning" }),

        // ── Troubleshooting ─────────────────────────────────────────────────────
        T("why-chances-open", "Troubleshooting",
            "Why did Conditions & Chances open?",
            "The dialog appears when a source's outgoing chances can no longer total 100%.",
            "Automatic after linking or deleting; also available from a node's right-click menu.",
            new[]
            {
                "A pool must total 100%. Typed values can leave no room for the automatic rows to absorb the remainder.",
                "The dialog header shows each pool's current total and marks the one that is off.",
                "Fix it by clearing typed values back to automatic, using Σ→100%, or adjusting individual rows.",
                "Cancel keeps the link or delete that triggered the check. Nothing is rewritten for you."
            },
            new[]
            {
                "A total over 100% means the typed rows alone exceed the pool; reduce or clear some values.",
                "A total under 100% with typed rows means the automatic rows cannot cover the gap."
            },
            new[] { "why", "chances", "open", "popup", "100", "warning", "not 100" }),

        T("link-wont-connect", "Troubleshooting",
            "A link won't connect",
            "Most link failures are duplicate routes or an invalid target.",
            "Combo graph → Connect to.",
            new[]
            {
                "The same source, target and input already exist, so the editor refuses a duplicate.",
                "Redirects and removed nodes cannot be targets. Pick a live move instead.",
                "On enemy graphs the input popup only offers Default (None). That is expected, not a failure.",
                "If Conditions & Chances opens instead, the link was added; close the dialog to keep it."
            },
            new[]
            {
                "Use Add Redirect via new node. when you need a route to pass through a helper node.",
                "Check the status bar: it names the reason when a link is rejected."
            },
            new[] { "link", "connect", "fail", "duplicate", "cannot", "target", "redirect" }),

        T("vanilla-removed", "Troubleshooting",
            "A vanilla node is marked removed",
            "Vanilla nodes are only crossed out at delete time; export drops them and rebalances the routes.",
            "Right-click a vanilla node → Delete node / Restore Node.",
            new[]
            {
                "Deleting a vanilla node marks it removed and strikes it through. The row stays in the model until export.",
                "Use Restore Node on the same menu to bring it back.",
                "On export the node is dropped and incoming chance pools are renormalized.",
                "If you want to control the new odds first, edit the remaining routes or type values in Conditions & Chances."
            },
            new[]
            {
                "Authored redirects pointing at a removed vanilla node are removed with it; the status message counts them.",
                "This is why vanilla deletions show a status hint instead of opening a dialog that cannot fix the still-present row."
            },
            new[] { "vanilla", "removed", "marked", "deleted", "strikethrough", "restore", "export" })
    };

    private static HelpTopic T(string id, string category, string title, string summary,
        string whereToFind, string[] steps, string[]? tips = null, string[]? keywords = null,
        string? tutorialId = null)
        => new(id, category, title, summary, whereToFind, steps, tips, keywords, tutorialId);
}
