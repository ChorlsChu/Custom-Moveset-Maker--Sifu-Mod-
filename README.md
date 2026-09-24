# Sifu Custom Moveset Maker

A modding tool for [Sifu](https://www.sloclap.com/games/sifu/) that lets you create, visualize, and export custom moveset mods — no UE4 editor required.

## What it does

- **Combo Tree Visualizer** — Displays the full attack combo tree as an interactive node graph. Click any node to preview its animation in a built-in 3D viewer. Drag-and-drop animations onto nodes to swap moves. Double-click nodes to reset to vanilla.

- **Animation Library** — Browse all loaded attack animations, search by name, and drag-and-drop to swap which animation a combo node plays. Filter by character, weapon type, and category (vanilla vs unused).

- **Edit Any Unit's Moveset** — Use **Change Unit** to switch between the player, enemies, and bosses. Works on MainChar weapon variants (Barehands, Bat, Staff, Blade) and on enemy/boss phases and weapon variants (Grunt, FireDisciple, FlashKick, BigGuy, BodyGuard, Fajar, Fengjie, Kuroki, Sean, Yang, Servant, Sifu). Edits on multiple units are kept in the project and export together in one pak.

- **Unit Properties (enemies & bosses)** — Via the **Unit Properties** button (not available for MainChar), tweak:
  - **ArchetypeDB**: Health and Structure
  - **ContextDefense (experimental)**: Memory Limit, Hits Count, and Flush Limit — how aggressively the AI parries, dodges, or avoids

- **Stance Tab** — View per-character combat stance animations for MainChar, Grunt, FireDisciple, FlashKick, BigGuy, BodyGuard, Fajar, Fengjie, Kuroki, Sean, Yang, and Servant. Switch stances via dropdown to apply BaseMovementDB + BP_TransitionAnimRequest.

- **Auto-Extract on First Run** — When the app opens and cannot find any extracted game files, it will automatically locate Sifu's original `.pak` file and selectively extract only the needed assets (animations, combo trees, attack data) into the app's directory. No need to manually extract the full 30GB game — just the ~500MB needed for modding.

- **Export Pak** — Package your modified combo tree into a UE4 `.pak` mod that installs into Sifu's `~mods` folder.

- **Import Mod** — Load existing mod paks for editing. Extracts the pak, detects stance, compares with vanilla, and restores original assets when done.

---

## How it works

Sifu stores its combat system in UE4 combo tree data assets. This tool parses those assets, lets you see and remix the moveset structure visually, and exports working mod paks — all from a standalone desktop app.

### Modding Flow

1. **Launch the app** — If no extracted content is found, the app will prompt you to locate Sifu's original `.pak` file and automatically extract only the needed assets into the app directory. This takes just a few minutes instead of extracting the full game.

2. **Visualize the combo tree** — See all nodes and edges of the Unit's attack graph. Nodes are color-coded:
   - **Blue**: Vanilla Attack Move
   - **Green**: Replaced Attack Move

3. **Pick the right unit** — Use **Change Unit** to select the player, an enemy, or a boss (and phase / weapon variant as needed) before editing.

4. **Edit moves** — Drag animations from the library onto combo nodes to replace moves. Double-click to preview any animation in the 3D viewer. Repeat across as many units as you want — each unit’s graph is cached in the project.

5. **Unit Properties (optional)** — For enemies and bosses (not MainChar), open **Unit Properties** to edit Health, Structure, and experimental AI defense fields (Memory Limit, Hits Count, Flush Limit). See [Unit Properties](#unit-properties).

6. **Switch stances** — Use the Combat Stance dropdown to replace the player's movement/stance with any of 12 supported stances. Each stance swaps:
   - `BaseMovementDB` — movement database with transition anim references
   - `BP_TransitionAnimRequest` — animation graph for locomotion transitions

7. **Export mod** — Generate a `.pak` + `.sig` file pair. The export pipeline:
   - Patches `m_Attacks` maps in each modified combo tree to reference new AttackDB assets
   - Copies/modifies animation `.uasset`/`.uexp` files (only modified ones)
   - Patches unit properties (ArchetypeDB Health/Structure, ContextualDefense) when changed
   - Patches stance DBs if stance changed (via StanceGenerator)
   - Builds UE4 pak using UnrealPak.exe (multi-unit mods export as one pak)
   - Copies .sig file for pak integrity

8. **Install** — Copy the generated pak+sig to `Sifu/Content/Paks/~mods/`

### Data Model

- **ComboNode**: Graph node with `AnimPath`, `DefaultAnimPath`, `DisplayName`, `DirectionLabel` (FL/FR/BL/BR/F/B/L/R)
- **ComboEdge**: Transition between nodes with `InputName` (LMB/RMB/S/Q/etc.)
- **AttackDB**: Self-contained move definition referencing an `AnimSequence` and `AttackDataRow` from a DataTable
- **AttackDataRow**: Struct holding per-attack stats (damage, knockback, hitframe, etc.)
- **BaseMovementDB**: Player movement database with `m_DetailedMoveTransitionDB`, `m_TransitionAnimRequest`, per-speed blend descriptions
- **BP_TransitionAnimRequest**: Blueprint asset defining which animations play for V0/V1 tense states, starts, and stops

### Supported Characters & Stances

The tool supports 12 stances, each with V0/V1 tense animations, blendspaces, and start/stop transitions:

| Stance | BaseMovementDB Path | Transition Asset |
|--------|---------------------|------------------|
| **MainChar** (default) | `DB/Movement/BaseMovementDB` | `DB/Movement/Transition/BP_TransitionAnimRequest` |
| **FireDisciple** | `DB/Movement/Archetypes/FireDisciple_BaseMovementDB` | `DB/Movement/Transition/BP_TransitionAnimRequest_FireDisciple` |
| **Grunt** | `DB/Movement/Archetypes/Grunt_BaseMovementDB` | `DB/Movement/Transition/BP_TransitionAnimRequest_Grunt` |
| **FlashKick** | `DB/Movement/Archetypes/FlashKick_BaseMovementDB` | `DB/Movement/Transition/BP_TransitionAnimRequest_Flashkick` |
| **BigGuy** | `DB/AI/Archetypes/BigGuy/BigGuy_BaseMovementDB` | `DB/Movement/Transition/BP_TransitionAnimRequest_BigGuy` |
| **BodyGuard** | `DB/Movement/Archetypes/Bodyguard_MovementDB` | `DB/Movement/Transition/BP_TransitionAnimRequest_Bodyguard` |
| **Fajar** | `Animations/Fajar/Fajar_BaseMovementDB` | (no transition) |
| **Fengjie** | `DB/Movement/Archetypes/Fengjie_BaseMovementDB` | `DB/Movement/Transition/BP_TransitionAnimRequestFengjie` |
| **Kuroki** | `DB/Movement/Archetypes/Kuroki_BaseMovementDB` | (no transition) |
| **Sean** | `DB/Movement/Archetypes/SeanBarehands_BaseMovementDB` | `DB/Movement/Transition/BP_TransitionAnimRequestSeanStaff` |
| **Servant** | `DB/Movement/Archetypes/Servant_BaseMovementDB` | `DB/Movement/Transition/BP_TransitionAnimRequest_Servant` |
| **Yang** | `DB/Movement/Archetypes/Yang_BaseMovementDB` | `DB/Movement/Transition/BP_TransitionAnimRequest_Yang` |

Stance animation mappings are defined in `Core/StanceGenerator.Stances` dictionary, containing V0/V1 tensed idle/blendspaces, and start/stop transition anim paths.

---

## Requirements

- Windows 10/11
- **.NET 10 Desktop Runtime** (framework-dependent build — install from Microsoft’s [dotnet.microsoft.com/download/dotnet/10.0](https://dotnet.microsoft.com/download/dotnet/10.0) if not present; Windows does **not** ship this by default)
- WebView2 Runtime — (pre-installed with Edge on Windows 10 1803+ and Windows 11)
- Sifu installed (via Epic Games Store or Steam)
- The app runs fully **offline** after install (3D viewer uses a local `viewer\three.min.js`; no CDN)

---

## Setup

### First-Time Experience

When you launch the app for the first time:

1. The app will start and automatically detect if extracted game content exists in the expected location.
2. **If no extracted content found**: You'll be prompted to locate Sifu's original `.pak` file (e.g., `pakchunk0-WindowsNoEditor.pak` found in the Sifu game installation folder).
3. The app will then use **UnrealPak** to selectively extract only the needed assets into the app's directory:
   - `Animations/` subfolders for all characters
   - `DB/_MainChar/Combos/`
   - `DB/AI/Archetypes/*/Attacks/`
   - `DB/Movement/`
   - *(Should take like around 1-2GB of space)*
4. Once extraction completes, the app will proceed to load animations, build the combo tree, and populate the animation library.
5. If extracted content is already present, the app will skip straight to loading.

### Subsequent Launches

- The app remembers the extracted content location in `settings.json`.
- On launch, it detects the content and proceeds directly to the main UI.
- To change content or re-extract, delete `settings.json` and restart the app, or use the **Settings** button to select a different game folder.

### Settings

- **ContentPath** — Path to the folder containing Sifu's `Content/` directory (or the extracted content folder).
- **OutputPath** — Where exported `.pak` + `.sig` files will be saved.
- **ShowLines** — Whether to show skeleton lines in the 3D animation viewer.
- **CameraPosition** / **CameraTarget** — Saved camera position/target for the 3D viewer (preserved between sessions).

The app writes `settings.json` in its installation directory on exit.

---

## Exporting a Mod

1. **Drag animations** onto combo nodes (or use right-click → Replace linked nodes with Switch). Edit as many units as you like — the review list includes every modified unit.
2. **Adjust Unit Properties** on enemies/bosses if desired (Health, Structure, AI defense).
3. **Switch stances** via the Combat Stance dropdown if desired.
4. Click **Export Pak** — the Export dialog will show:
   - **Review panel**: list of all changes (modified nodes + unit property values + stance changes).
   - **Progress**: step-by-step build (copy files → patch imports → build pak).
   - **Complete**: shows pak size, change count, and install location.
5. Copy the generated pak+sig (e.g. `MainCharComboMod.pak` or `MultiUnitComboMod.pak`) to `Sifu/Content/Paks/~mods/`.
6. Launch Sifu — your new moveset should be active!

**Note**: The export pipeline only packages assets that were actually modified. If you made no changes, it will prompt you to drag animations onto nodes first.

---

## Unit Properties

Available for **enemies and bosses only** — the button is hidden when MainChar is active.

| Section | Field | What it does |
|---------|--------|----------------|
| **ArchetypeDB** | Health | Max HP. At zero, the unit is defeated. |
| **ArchetypeDB** | Structure | Posture bar. When full, the unit staggers and takes bonus damage; resets over time. |
| **ContextDefense** *(experimental)* | Memory Limit | Seconds the AI “remembers” hits for defense triggers. |
| **ContextDefense** *(experimental)* | Hits Count | How many hits within the window before it parries, dodges, or avoids. |
| **ContextDefense** *(experimental)* | Flush Limit | Cooldown (seconds) after a defense before it can defend again. |

- Values are read from the unit’s **ArchetypeDB** (Health/Structure) and **ContextualDefense** asset (AI fields).
- Changes appear in the export review list and are patched into those assets on export.
- **Reset to Defaults** restores vanilla values for the current unit.
- AI defense is **experimental**: vanilla defaults differ by enemy, and some units expose fewer defense knobs than others. Test in-game after export.

---

## Importing a Mod

1. Click **Import Mod** and select a `.pak` file.
2. The tool will:
   - Extract the pak using UnrealPak (or use previously extracted content).
   - Detect the stance from transition assets.
   - Load the modded combo tree and compare with vanilla.
   - Show all changes in a review panel.
3. Edit further if desired, or just save the project and re-export.

---

## Project Files

- **`.sifu-edit`** — Project save format: node animation swaps per unit, unit properties, and multi-unit caches (JSON).
- **`.pak` + `.sig`** — Mod package (UE4 pak + integrity signature).
- **`settings.json`** — Persistent app settings (content path, output path, camera position, stance).

---

## Supported Animations

The tool scans and validates animations from these DataTable paths:
- `Game/DB/AI/Archetypes/*/Attacks/*/*_AttacksDatatable` (Grunt, FireDisciple, FlashKick, BigGuy, BodyGuard, Fajar, Sean, Kuroki, Fengjie, Yang)
- `Game/DB/Attacks/WUGUAN_Attacks` (empty/0 rows)

Each AttackDataRow contains fields: `m_Anim` (SoftObjectProperty), `m_bCanBeMirrored`, `m_eMirroringMethod`, `m_fMirrorUseNextFootMarkerThreshold`, `m_eStartQuadrant`, `m_eEndQuadrant`, `m_fMeasuredForwardMovement`, `m_fMovementRightLength`, `m_bDirectRightMovementFollow`, `m_iLastBuildupFrame`, `m_fHitFrame`, `m_bStrikelessAttack`, `m_Name`, `m_RealAttackName`.

The tool also scans `Animations/` directory for per-character animation files, organized by weapon type and category (LightCombo, HeavyCombo, GetUp, Skill, etc.).

---

## Auto-Extract Feature (How It Works)

On first run, if the app cannot find valid extracted content:

1. **Prompt for game pak** — User selects Sifu's original `.pak` file (e.g., located in `C:\Program Files\Sifu\Content\Paks\` or similar).
2. **Selective extraction** — The app runs UnrealPak with arguments to extract only needed directories:
   - `Animations/*/`, `DB/_MainChar/Combos/`, `DB/AI/Archetypes/*/Attacks/`, `DB/Movement/`
   - Files with `.uasset` and `.uexp` extensions only (`.bak` and other artifacts are skipped)
   - Approximately 1.2GB of data extracted
3. **Placement** — Extracted files are placed in the app's directory structure, e.g.:
   ```
   SifuMovesetEditor/
   ├── extracted/
   │   ├── Animations/
   │   │   ├── MainChar/
   │   │   ├── FireDisciple/
   │   │   └─ ...
   │   ├── DB/
   │   │   ├── _MainChar/Combos/
   │   │   └─ ...
   │   └─ ...
   ├── settings.json
   └─ ...
   ```
4. **Proceed** — The app loads the extracted content and shows the main UI. On subsequent launches, it detects the extracted content and skips the extraction prompt.

---

## Credits

- [Sloclap](https://www.sloclap.com/) — Developer and publisher of Sifu. This tool is an independent fan-made modding utility and is not affiliated with or endorsed by Sloclap.

---

## License

This tool is provided as-is for educational and modding purposes. You must own a legal copy of Sifu to use it.

---

## Status

**Prototype / work in progress.** The import mod process might need more testing to make sure it is fully working on all mods.


## Planned Enhancements (future roadmap)

- Custom chain attacks on MainCharacter. Doing that on enemy movesets is causing the game to crash for some reason
- Custom Arena level creator where you can modify the waves, enemy spawns and all that (Maybe I'll make this as a standalone, unsure)

---

## FAQ

**Q: Do I need to extract the full Sifu game to use this tool?**  
A: No! The auto-extract feature on first run will selectively extract only the ~500MB of assets needed (animations, combo trees, attack data). You do not need to extract the full 30GB game.

**Q: What if I already extracted the game content manually?**  
A: You can point the app directly to your manually extracted folder via Settings.

**Q: Can I use this tool without owning Sifu?**  
A: No. You must own a legal copy of Sifu to use this modding tool, as it requires access to the game's original assets.

**Q: Will the extracted files be left in my app directory forever?**  
A. Yes, they persist between sessions to avoid re-extracting. If you want to re-extract or change the source, delete `settings.json` and restart the app, or use the Settings dialog to select a different game folder.

**Q: Can I extract to a different location than the app directory?**  
A: The auto-extract feature places extracted content in a subfolder within the app's directory. For custom locations, use the manual Setup workflow: point the app to your existing `Content/` folder via Settings.

**Q: Can I edit boss or enemy health and defense?**  
A: Yes — switch to the unit via Change Unit, then open **Unit Properties** (hidden for MainChar). You can edit Health and Structure, plus experimental AI defense fields (Memory Limit, Hits Count, Flush Limit). Changes export with your mod.

**Q: Can I edit movesets for all enemies and bosses in one mod?**  
A: Yes. Switch units with Change Unit, edit each moveset (and unit properties if you want), then export once — all modified units are packaged into a single pak.

---

**Go wild with your movesets!** 🥋