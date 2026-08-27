# Sifu Custom Moveset Maker

A modding tool for [Sifu](https://www.sloclap.com/games/sifu/) that lets you create, visualize, and export custom moveset mods — no UE4 editor required.

## What it does

- **Combo Tree Visualizer** — Displays the full attack combo tree as an interactive node graph. Click any node to preview its animation in a built-in 3D viewer.
- **Animation Library** — Browse all loaded attack animations, search by name, and drag-and-drop to swap which animation a combo node plays.
- **Locomotion Tab** — View per-character combat stance animations for MainChar, Grunt, FireDisciple, FlashKick, BigGuy, BodyGuard, and all bosses.
- **Export Pak** — Package your modified combo tree into a UE4 `.pak` mod that installs into Sifu's `~mods` folder.

## How it works

Sifu stores its combat system in UE4 combo tree data assets. This tool parses those assets, lets you see and remix the moveset structure visually, and exports working mod paks — all from a standalone desktop app.

## Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later (for building)
- [WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (for the 3D animation viewer — pre-installed with Edge on Windows 10 1803+ and Windows 11)
- [Sifu](https://www.sloclap.com/games/sifu/) installed (owned via Epic Games Store)
- The game's extracted content files (see [Setup](#setup) below)

## Setup

1. Clone or download this repository
2. Build with Visual Studio 2022+ (`dotnet build` or open the `.sln`)
3. Point the tool to your extracted Sifu content (the `Content/` folder from the game's pak files)
4. The tool needs ~460 MB of game assets: combo trees, attack data tables, character meshes, and animations

## Status

Prototype / work in progress. The combo graph viewer, animation library, and locomotion viewer are functional. The export pipeline is being refined.

## Credits

- [Sloclap](https://www.sloclap.com/) — Developer and publisher of Sifu. This tool is an independent fan-made modding utility and is not affiliated with or endorsed by Sloclap.

## License

This tool is provided as-is for educational and modding purposes. You must own a legal copy of Sifu to use it.
