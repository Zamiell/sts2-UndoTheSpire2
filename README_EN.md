# Undo the Spire 2

**Language:** [中文](./README.md) | **English**

`Undo the Spire 2` is an in-combat undo / redo mod for *Slay the Spire 2*.  
It is not a full time-rewind system. The goal is to provide stable rollback for common misplays in single-player combat.

## Features

- Multi-step `Undo / Redo` during combat
- Always-visible HUD buttons
- `Ctrl+Z / Ctrl+Y` hotkeys
- Instant rollback for normal card plays, potion use, potion discard, and end turn
- Undo support for `Choose A Card` and hand-selection screens
- In some card-choice cases, you can undo and pick a different option

## Not Supported

- Map, events, shop, campfire, or post-combat rewards
- Multiplayer
- Persisting undo history across restarts
- Controller-only interaction flow

## Download

- [Go to the Releases page](../../releases)
- Release packages usually include `UndoTheSpire2.dll` and `UndoTheSpire2.json`

## Installation

1. Download a release, or build the mod yourself to get `UndoTheSpire2.dll` and `UndoTheSpire2.json`
2. Put both files into the game's `mods` folder
3. If you still have an old `UndoTheSpire2.pck` from an older version, remove it first

## How To Use

- Left-click the HUD button: `Undo`
- Right-click the HUD button: `Redo`
- Hotkeys: `Ctrl+Z` / `Ctrl+Y`
- Hovering the buttons shows the latest available undo / redo action

## Notes

- This mod focuses on practical rollback for common single-player combat mistakes, not universal support for every future card, potion, or choice type
- Some complex choice flows, effects, or animations may still behave unexpectedly
- If rollback produces the wrong state, please report the exact card, potion, relic, and reproduction steps

## Feedback

Bug reports are much easier to investigate if you include:

- The card / potion / relic involved
- Reproduction steps
- The game log

The log is typically located at:

`C:\Users\YourUserName\AppData\Roaming\SlayTheSpire2\undo-the-spire2`

## Developer Version Notes

- Current compatibility target: **Slay the Spire 2 v0.111.0** and **.NET 9**. Older game versions are not supported by this build.
- The project references the live game DLL through `$(Sts2DataDir)/sts2.dll`; update source notes and API checks when the game version changes.
- Runtime-sensitive v0.106 cases are documented in `ARCHITECTURE.md`: Aeonglass/Wither fake upgrades and ThinkingAhead hand-selection replay.
- v0.111.0 stores combat turn flags and ready signals in `CombatTurnState`. Restore cancels the discarded turn loop, replaces that state, and resumes after turn-start effects instead of replaying them.

The managed compatibility checks initialize the snapshot codecs, install every Harmony patch, and exercise restored turn progression without launching the game. Run them with a .NET 9 runtime (the game's Harmony version does not support .NET 10):

```sh
dotnet run --project tests/Compatibility -c Release \
  -p:Sts2Path= -p:Sts2DataDir="/path/to/game/data_sts2_windows_x86_64" \
  -- "/path/to/game/data_sts2_windows_x86_64"
```

Set `NUGET_PACKAGES` to a writable local package cache if the development path in `NuGet.Config` is unavailable. The empty `Sts2Path` prevents the build from deploying a second copy of the mod. To install manually, replace the DLL in the existing mod folder and restart the game. These checks do not replace in-game UI and card-effect testing.
