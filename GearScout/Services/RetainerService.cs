using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace GearScout.Services;

public sealed class RetainerService
{
    public unsafe RetainerInfo? GetLastSelectedRetainer()
    {
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady || manager->LastSelectedRetainerId == 0)
            return null;

        var retainer = manager->GetActiveRetainer();
        if (retainer == null || retainer->RetainerId == 0)
            return null;

        return new RetainerInfo(retainer->RetainerId, retainer->NameString, retainer->ClassJob, retainer->Level);
    }

    public unsafe bool HasActiveRetainerSession()
    {
        var manager = RetainerManager.Instance();
        return manager != null && manager->IsReady && manager->RetainerObjectId != 0 && manager->LastSelectedRetainerId != 0;
    }

    public unsafe IReadOnlyDictionary<ulong, RetainerInfo> GetRetainers()
    {
        var result = new Dictionary<ulong, RetainerInfo>();
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady)
            return result;

        var count = manager->GetRetainerCount();
        for (uint i = 0; i < count; i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);
            if (retainer == null || retainer->RetainerId == 0)
                continue;

            result[retainer->RetainerId] = new RetainerInfo(
                retainer->RetainerId,
                retainer->NameString,
                retainer->ClassJob,
                retainer->Level);
        }

        return result;
    }
}
