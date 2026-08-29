using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using GearScout.Services;
using GearScout.Windows;

namespace GearScout;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManagerService { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Configuration { get; }
    public IDataManager DataManager => DataManagerService;
    public AllaganToolsService AllaganTools { get; }
    public RetainerService RetainerService { get; }
    public GearRecommendationService RecommendationService { get; }
    public PlanService PlanService { get; }

    private readonly WindowSystem windowSystem = new("GearScout");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private bool retainerEquipmentContext;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        AllaganTools = new AllaganToolsService(PluginInterface, Log);
        RetainerService = new RetainerService();
        RecommendationService = new GearRecommendationService(DataManagerService, Configuration, AllaganTools, RetainerService);
        PlanService = new PlanService(Configuration, AllaganTools);

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);

        CommandManager.AddHandler("/gearscout", new CommandInfo(OnMainCommand)
        {
            HelpMessage = "Open GearScout. Use '/gearscout config' for settings.",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Character", OnCharacterSetup);
        AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "Character", OnCharacterDraw);
        AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Character", OnCharacterFinalize);
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerCharacter", OnRetainerSetup);
        AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "RetainerCharacter", OnRetainerDraw);
        AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "RetainerCharacter", OnRetainerFinalize);

        if (Configuration.ActivePlan != null && Configuration.KeepOpenWhilePlanActive)
            mainWindow.IsOpen = true;

        Log.Information("GearScout loaded (API 15 beta)");
    }

    public void Dispose()
    {
        AddonLifecycle.UnregisterListener(OnCharacterSetup, OnCharacterDraw, OnCharacterFinalize, OnRetainerSetup, OnRetainerDraw, OnRetainerFinalize);
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        CommandManager.RemoveHandler("/gearscout");
        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        configWindow.Dispose();
    }

    public GearTarget? GetEffectiveTarget()
    {
        if (Configuration.ActivePlan != null)
            return Configuration.ActivePlan.ToTarget();

        if (retainerEquipmentContext)
        {
            var retainer = RetainerService.GetLastSelectedRetainer();
            if (retainer != null)
                return new GearTarget(retainer.Id, retainer.Name, retainer.JobId, retainer.Level, true);
        }

        if (!PlayerState.IsLoaded || !PlayerState.ClassJob.IsValid)
            return null;

        var characterId = AllaganTools.CurrentCharacterId;
        if (characterId == 0)
            characterId = PlayerState.ContentId;
        if (characterId == 0)
            return null;

        return new GearTarget(
            characterId,
            string.IsNullOrWhiteSpace(PlayerState.CharacterName) ? "Current character" : PlayerState.CharacterName,
            PlayerState.ClassJob.RowId,
            PlayerState.Level > 0 ? (uint)PlayerState.Level : 0u,
            false);
    }

    public void ToggleConfigUi() => configWindow.Toggle();
    public void ToggleMainUi() => mainWindow.Toggle();
    public void RequestRefresh() => mainWindow.RequestRefresh();
    public void ReleaseWindowAnchor() => mainWindow.ReleaseAnchor();

    private void OnMainCommand(string command, string args)
    {
        if (args.Trim().Equals("config", StringComparison.OrdinalIgnoreCase))
            ToggleConfigUi();
        else
            ToggleMainUi();
    }

    private void OnCharacterSetup(AddonEvent type, AddonArgs args)
    {
        // The game's RetainerManager can keep LastSelectedRetainerId/RetainerObjectId populated
        // after leaving the bell. A real Character addon must therefore always mean the player.
        SetEquipmentContext(isRetainer: false);
        if (Configuration.AutoOpenWithCharacterWindow)
            mainWindow.IsOpen = true;
        UpdateAnchor(args);
    }

    private void OnCharacterDraw(AddonEvent type, AddonArgs args)
    {
        // Reinforce the player context while the native Character window is visibly being drawn.
        // This prevents a stale retainer session from ever stealing the target after plugin reloads.
        SetEquipmentContext(isRetainer: false);
        UpdateAnchor(args);
    }

    private void OnCharacterFinalize(AddonEvent type, AddonArgs args)
    {
        retainerEquipmentContext = false;
        mainWindow.ReleaseAnchor();
        HandleEquipmentWindowClosed();
    }

    private void OnRetainerSetup(AddonEvent type, AddonArgs args)
    {
        SetEquipmentContext(isRetainer: true);
        if (Configuration.AutoOpenWithRetainerEquipment)
            mainWindow.IsOpen = true;
        UpdateAnchor(args);
    }

    private void OnRetainerDraw(AddonEvent type, AddonArgs args)
    {
        SetEquipmentContext(isRetainer: true);
        UpdateAnchor(args);
    }

    private void OnRetainerFinalize(AddonEvent type, AddonArgs args)
    {
        retainerEquipmentContext = false;
        mainWindow.ReleaseAnchor();
        HandleEquipmentWindowClosed();
    }

    private void SetEquipmentContext(bool isRetainer)
    {
        if (retainerEquipmentContext == isRetainer)
            return;

        retainerEquipmentContext = isRetainer;
        RequestRefresh();
    }

    private void UpdateAnchor(AddonArgs args)
    {
        if (!Configuration.AnchorToEquipmentWindow || args.Addon.IsNull || !args.Addon.IsVisible)
            return;

        mainWindow.AnchorTo(args.Addon.Position, args.Addon.ScaledWidth);
    }

    private void HandleEquipmentWindowClosed()
    {
        if (Configuration.ActivePlan != null && Configuration.KeepOpenWhilePlanActive)
        {
            mainWindow.IsOpen = true;
            return;
        }

        if (Configuration.CloseWhenGameWindowCloses)
            mainWindow.IsOpen = false;
    }
}
