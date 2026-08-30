# GearScout

GearScout is a Dalamud plugin that extends FFXIV's Recommended Gear idea across everything you already own.

Instead of checking only the equipment currently available to the game, GearScout uses Allagan Tools' inventory index to consider your Inventory, Armoury Chest, chocobo saddlebag, retainers, Glamour Dresser and Armoire, then helps you retrieve the pieces you actually want to equip.

## Features

- Recommended Gear-style ranking for the current character/job or a retainer.
- Global inventory lookup through Allagan Tools.
- Job-aware recommendation scoring for combat jobs, crafters and gatherers (not an endgame BiS solver).
- Optional protection for equipment already referenced by gearsets.
- Persistent retrieve → ready → equipped plans.
- Compact independent retrieval checklist after the native equipment window closes.
- Item icons, source labels and per-line state colours/glyphs.
- Native Glamour Dresser highlighting for visible cells that belong to the active plan.
- Optional hints for unopened equipment coffers / attire using only FFXIV's native item metadata. These are deliberately shown as *potential* gear sources; GearScout never invents exact contents and never opens a coffer automatically.
- Independent settings window available from Dalamud or `/gearscout config`.

## Installation

Add the following URL to **Dalamud Settings → Experimental → Custom Plugin Repositories**:

```
https://raw.githubusercontent.com/Arceuid731/GearScout/main/pluginmaster.json
```

Then search for **GearScout** in the Dalamud plugin installer.

GearScout currently requires **Allagan Tools** to be installed and enabled.

## Commands

- `/gearscout` — toggle the main GearScout window.
- `/gearscout config` — open settings.

## Retrieval workflow

1. Open Character or a retainer's equipment screen.
2. Tick the recommended pieces you actually want.
3. Close the native equipment screen; GearScout becomes a compact persistent checklist.
4. Retrieve the indicated items. GearScout changes them from pending to ready automatically once they reach your Inventory / Armoury Chest.
5. Re-open the target and equip them; completed lines are detected automatically.

When **Highlight** is enabled, planned items currently visible in the Glamour Dresser are outlined directly in the native dresser grid to make large collections easier to search.

## Notes

GearScout is intentionally read-only. It does not move gear, equip items, or open coffers automatically. Remote inventory accuracy depends on the snapshots Allagan Tools has seen, so visit/open remote storages normally when you want them refreshed.

GearScout is still beta software and native UI integrations such as Glamour Dresser highlighting can need adjustment after FFXIV/Dalamud updates.
