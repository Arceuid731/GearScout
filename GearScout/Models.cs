using System;
using System.Collections.Generic;
using System.Linq;

namespace GearScout;

public enum GearSlot
{
    MainHand,
    OffHand,
    Head,
    Body,
    Hands,
    Legs,
    Feet,
    Ears,
    Neck,
    Wrists,
    RingLeft,
    RingRight,
}

public enum ItemSourceKind
{
    Unknown,
    EquippedTarget,
    PlayerInventory,
    Armoury,
    Chocobo,
    GlamourDresser,
    Armoire,
    Retainer,
    EquippedElsewhere,
}

public enum PlanItemState
{
    ToRetrieve,
    Retrieved,
    Equipped,
    Missing,
}

public enum ReservedItemPolicy
{
    Exclude,
    ShowOnly,
    Allow,
}

public sealed record GearTarget(ulong Id, string Name, uint JobId, uint Level, bool IsRetainer)
{
    public string Key => $"{(IsRetainer ? "retainer" : "player")}:{Id}";
}

public sealed class InventoryEntry
{
    public ulong CharacterId { get; init; }
    public uint Container { get; init; }
    public short Slot { get; init; }
    public uint ItemId { get; init; }
    public uint Quantity { get; init; }
    public ulong Flags { get; init; }
    public uint SortedContainer { get; init; }
    public int SortedSlotIndex { get; init; }
    public ulong RetainerId { get; init; }
    public uint[] GearSets { get; init; } = Array.Empty<uint>();
    public string Fingerprint { get; init; } = string.Empty;

    public bool IsHighQuality => (Flags & 1UL) != 0;
    public bool InGearSet => GearSets.Length > 0;

    public static InventoryEntry? FromSerialized(ulong characterId, ulong[] data)
    {
        if (data.Length < 25 || data[2] == 0)
            return null;

        var gearSets = data.Length > 25
            ? data.Skip(25).Select(x => (uint)x).ToArray()
            : Array.Empty<uint>();

        var fingerprintParts = new List<ulong>
        {
            data[2], data[6],
            data[7], data[8], data[9], data[10], data[11],
            data[12], data[13], data[14], data[15], data[16],
            data[17], data[18], data[19],
        };

        return new InventoryEntry
        {
            CharacterId = characterId,
            Container = (uint)data[0],
            Slot = unchecked((short)data[1]),
            ItemId = (uint)data[2],
            Quantity = (uint)data[3],
            Flags = data[6],
            SortedContainer = (uint)data[20],
            SortedSlotIndex = unchecked((int)data[22]),
            RetainerId = data[23],
            GearSets = gearSets,
            Fingerprint = string.Join(':', fingerprintParts),
        };
    }
}

public sealed class RecommendationCandidate
{
    public required InventoryEntry Entry { get; init; }
    public required string ItemName { get; init; }
    public required GearSlot Slot { get; init; }
    public required uint ItemLevel { get; init; }
    public required uint EquipLevel { get; init; }
    public required ItemSourceKind SourceKind { get; init; }
    public required string SourceLabel { get; init; }
    public bool ReservedByGearSet => Entry.InGearSet;
}

public sealed class RecommendationRow
{
    public required GearSlot Slot { get; init; }
    public RecommendationCandidate? Recommended { get; init; }
    public RecommendationCandidate? BetterReservedCandidate { get; init; }
}

public sealed class PlannedGearItem
{
    public GearSlot Slot { get; set; }
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public ulong Flags { get; set; }
    public uint ItemLevel { get; set; }
    public ulong OriginalCharacterId { get; set; }
    public uint OriginalContainer { get; set; }
    public string OriginalSourceLabel { get; set; } = string.Empty;
    public string CurrentSourceLabel { get; set; } = string.Empty;
    public PlanItemState State { get; set; } = PlanItemState.ToRetrieve;
}

public sealed class GearPlan
{
    public string TargetKey { get; set; } = string.Empty;
    public ulong TargetId { get; set; }
    public string TargetName { get; set; } = string.Empty;
    public uint JobId { get; set; }
    public uint Level { get; set; }
    public bool IsRetainer { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<PlannedGearItem> Items { get; set; } = new();

    public GearTarget ToTarget() => new(TargetId, TargetName, JobId, Level, IsRetainer);
}

public sealed record RetainerInfo(ulong Id, string Name, uint JobId, uint Level);
