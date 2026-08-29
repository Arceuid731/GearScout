# GearScout

GearScout is a Dalamud plugin for Final Fantasy XIV that extends the game's **Recommended Gear** workflow across the gear you already own.

Instead of only looking at immediately available inventory/Armoury items, GearScout uses **Allagan Tools** inventory snapshots to search your inventory, Armoury Chest, chocobo saddlebag, retainers, glamour dresser and armoire.

> Early beta. GearScout is deliberately read-only: it never moves or equips items automatically.

## What the beta does

- Detects the current player job/level or the retainer whose equipment is being viewed.
- Recommends equippable gear using a simple Recommended Gear-style priority: item level first, not BiS/stat-weight optimization.
- Shows where every recommended item is stored.
- Handles the two ring slots as distinct physical items.
- Lets you decide how gear already referenced by gearsets is treated: exclude it, show it as reserved, or allow it.
- Can exclude gear currently equipped by another character/retainer.
- Lets each storage source be enabled/disabled independently.
- Builds a persistent gear plan from the pieces you select.
- Keeps that plan visible after the target equipment window closes.
- Tracks each selected piece through **To retrieve → Retrieved → Equipped**.
- Automatically treats player inventory/Armoury as “retrieved”.
- Groups the checklist by storage location so you can visit each retainer/storage once.
- Keeps a chosen plan stable until you explicitly recalculate it.
- Has a separate settings window available from Dalamud's plugin list.

## Dependency

GearScout currently requires **Allagan Tools** to be installed and enabled. Allagan Tools is the global inventory index; GearScout consumes its public IPC data rather than maintaining a second competing inventory database.

Remote inventories are snapshots. Open your retainers, glamour dresser, armoire and other storage normally at least once so Allagan Tools has current data for them.

## Installation

Once a beta release exists, add this URL under **Dalamud Settings → Experimental → Custom Plugin Repositories**:

```text
https://raw.githubusercontent.com/Arceuid731/GearScout/main/pluginmaster.json
```

Then open the Plugin Installer, search for **GearScout**, and install it.

The `/gearscout` command toggles the main window. `/gearscout config` opens settings.

## Build

GearScout targets Dalamud API 15 / .NET 10 via `Dalamud.NET.Sdk/15.0.0`.

```powershell
dotnet build GearScout.slnx --configuration Release
```

GitHub Actions also builds every pull request and publishes a rolling `latest` beta release after a successful push to `main`.
