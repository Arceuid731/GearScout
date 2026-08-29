using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace GearScout.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public ConfigWindow(Plugin plugin)
        : base("GearScout Settings##GearScoutConfig")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(430, 420),
            MaximumSize = new Vector2(760, 900),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var config = plugin.Configuration;
        var changed = false;

        ImGui.TextWrapped("GearScout stays read-only: it recommends, locates and tracks gear, but never moves or equips items automatically.");
        ImGui.Spacing();

        Section("Reserved / equipped items");
        ImGui.Text("Items used by gearsets");
        ImGui.SameLine();
        var preview = config.GearsetItems switch
        {
            ReservedItemPolicy.Exclude => "Exclude",
            ReservedItemPolicy.ShowOnly => "Show as reserved",
            ReservedItemPolicy.Allow => "Allow",
            _ => "Show as reserved",
        };
        if (ImGui.BeginCombo("##GearsetPolicy", preview))
        {
            foreach (var value in Enum.GetValues<ReservedItemPolicy>())
            {
                var label = value switch
                {
                    ReservedItemPolicy.Exclude => "Exclude from results",
                    ReservedItemPolicy.ShowOnly => "Show better reserved gear, but don't recommend it",
                    ReservedItemPolicy.Allow => "Allow as recommended gear",
                    _ => value.ToString(),
                };

                var selected = config.GearsetItems == value;
                if (ImGui.Selectable(label, selected))
                {
                    config.GearsetItems = value;
                    changed = true;
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        changed |= Checkbox("Include gear equipped by another target", config.IncludeEquippedElsewhere, v => config.IncludeEquippedElsewhere = v);
        ImGui.TextDisabled("Disabled by default so GearScout won't suggest stripping another retainer/character.");

        Section("Inventory sources");
        changed |= Checkbox("Player inventory", config.IncludePlayerInventory, v => config.IncludePlayerInventory = v);
        changed |= Checkbox("Armoury Chest", config.IncludeArmoury, v => config.IncludeArmoury = v);
        changed |= Checkbox("Chocobo saddlebag", config.IncludeChocobo, v => config.IncludeChocobo = v);
        changed |= Checkbox("Retainers", config.IncludeRetainers, v => config.IncludeRetainers = v);
        changed |= Checkbox("Glamour dresser", config.IncludeGlamourDresser, v => config.IncludeGlamourDresser = v);
        changed |= Checkbox("Armoire", config.IncludeArmoire, v => config.IncludeArmoire = v);

        Section("Window behaviour");
        changed |= Checkbox("Auto-open with Character window", config.AutoOpenWithCharacterWindow, v => config.AutoOpenWithCharacterWindow = v);
        changed |= Checkbox("Auto-open with retainer equipment", config.AutoOpenWithRetainerEquipment, v => config.AutoOpenWithRetainerEquipment = v);
        changed |= Checkbox("Keep GearScout open while a plan is active", config.KeepOpenWhilePlanActive, v => config.KeepOpenWhilePlanActive = v);
        changed |= Checkbox("Close with the game equipment window when no plan is active", config.CloseWhenGameWindowCloses, v => config.CloseWhenGameWindowCloses = v);
        changed |= Checkbox("Only show actual upgrades", config.ShowOnlyUpgrades, v => config.ShowOnlyUpgrades = v);

        Section("Data source");
        if (plugin.AllaganTools.IsAvailable)
            ImGui.TextColored(new Vector4(0.35f, 0.9f, 0.45f, 1f), "Allagan Tools IPC: connected");
        else
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), "Allagan Tools IPC: unavailable");
        ImGui.TextWrapped("Allagan Tools is required for the global inventory view. Open each remote storage normally so Allagan Tools has had a chance to snapshot it.");

        if (plugin.Configuration.ActivePlan != null)
        {
            Section("Active plan");
            ImGui.Text($"{plugin.Configuration.ActivePlan.TargetName} — {plugin.Configuration.ActivePlan.Items.Count} selected item(s)");
            if (ImGui.Button("Clear active plan"))
            {
                plugin.PlanService.Clear();
                plugin.RequestRefresh();
            }
        }

        if (changed)
        {
            config.Save();
            plugin.RequestRefresh();
        }
    }

    private static void Section(string title)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted(title);
    }

    private static bool Checkbox(string label, bool value, Action<bool> setter)
    {
        var edited = value;
        if (!ImGui.Checkbox(label, ref edited) || edited == value)
            return false;

        setter(edited);
        return true;
    }
}
