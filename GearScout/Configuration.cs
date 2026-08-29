using Dalamud.Configuration;
using Dalamud.Plugin;

namespace GearScout;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public ReservedItemPolicy GearsetItems { get; set; } = ReservedItemPolicy.ShowOnly;
    public bool IncludeEquippedElsewhere { get; set; } = false;

    public bool IncludePlayerInventory { get; set; } = true;
    public bool IncludeArmoury { get; set; } = true;
    public bool IncludeChocobo { get; set; } = true;
    public bool IncludeRetainers { get; set; } = true;
    public bool IncludeGlamourDresser { get; set; } = true;
    public bool IncludeArmoire { get; set; } = true;

    public bool AutoOpenWithCharacterWindow { get; set; } = true;
    public bool AutoOpenWithRetainerEquipment { get; set; } = true;
    public bool KeepOpenWhilePlanActive { get; set; } = true;
    public bool CloseWhenGameWindowCloses { get; set; } = false;
    public bool ShowOnlyUpgrades { get; set; } = false;

    public GearPlan? ActivePlan { get; set; }

    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;
    public void Save() => pluginInterface?.SavePluginConfig(this);
}
