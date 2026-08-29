using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace GearScout.Services;

public sealed class AllaganToolsService
{
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<bool> isInitialized;
    private readonly ICallGateSubscriber<ulong> currentCharacter;
    private readonly ICallGateSubscriber<bool, HashSet<ulong>> ownedCharacters;
    private readonly ICallGateSubscriber<ulong, HashSet<ulong[]>> characterItems;

    public AllaganToolsService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        isInitialized = pluginInterface.GetIpcSubscriber<bool>("AllaganTools.IsInitialized");
        currentCharacter = pluginInterface.GetIpcSubscriber<ulong>("AllaganTools.CurrentCharacter");
        ownedCharacters = pluginInterface.GetIpcSubscriber<bool, HashSet<ulong>>("AllaganTools.GetCharactersOwnedByActive");
        characterItems = pluginInterface.GetIpcSubscriber<ulong, HashSet<ulong[]>>("AllaganTools.GetCharacterItems");
    }

    public bool IsAvailable
    {
        get
        {
            try
            {
                return isInitialized.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    public ulong CurrentCharacterId
    {
        get
        {
            try
            {
                return currentCharacter.InvokeFunc();
            }
            catch
            {
                return 0;
            }
        }
    }

    public IReadOnlyList<InventoryEntry> GetAllItems()
    {
        var output = new List<InventoryEntry>();
        if (!IsAvailable)
            return output;

        try
        {
            var ids = ownedCharacters.InvokeFunc(true);
            foreach (var id in ids)
            {
                HashSet<ulong[]> serialized;
                try
                {
                    serialized = characterItems.InvokeFunc(id);
                }
                catch (Exception ex)
                {
                    log.Debug(ex, "Could not read Allagan Tools items for {CharacterId}", id);
                    continue;
                }

                foreach (var raw in serialized)
                {
                    var parsed = InventoryEntry.FromSerialized(id, raw);
                    if (parsed != null)
                        output.Add(parsed);
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not enumerate Allagan Tools inventories");
        }

        return output;
    }
}
