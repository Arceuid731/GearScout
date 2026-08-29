using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace GearScout.Services;

public sealed class GearRecommendationService
{
    private readonly IDataManager dataManager;
    private readonly Configuration config;
    private readonly AllaganToolsService allagan;
    private readonly RetainerService retainers;
    private readonly Dictionary<(uint CategoryId, uint JobId), bool> jobCompatibilityCache = new();

    public GearRecommendationService(IDataManager dataManager, Configuration config, AllaganToolsService allagan, RetainerService retainers)
    {
        this.dataManager = dataManager;
        this.config = config;
        this.allagan = allagan;
        this.retainers = retainers;
    }

    public IReadOnlyList<RecommendationRow> Build(GearTarget target, IReadOnlyList<InventoryEntry>? inventory = null)
    {
        inventory ??= allagan.GetAllItems();
        var retainerMap = retainers.GetRetainers();
        var buckets = Enum.GetValues<GearSlot>().ToDictionary(x => x, _ => new List<RecommendationCandidate>());
        // Gearset reservations exist mainly to avoid stripping the player's saved sets to outfit a retainer.
        // For the player themself, a piece referenced by a gearset is still a perfectly valid Recommended Gear candidate.
        var gearsetPolicy = target.IsRetainer ? config.GearsetItems : ReservedItemPolicy.Allow;

        foreach (var entry in inventory)
        {
            if (!TryBuildCandidateBase(target, entry, retainerMap, out var item, out var sourceKind, out var sourceLabel))
                continue;

            var slots = GetSlots(item);
            foreach (var slot in slots)
            {
                buckets[slot].Add(new RecommendationCandidate
                {
                    Entry = entry,
                    ItemName = item.Name.ToString(),
                    Slot = slot,
                    ItemLevel = item.LevelItem.RowId,
                    EquipLevel = item.LevelEquip,
                    SourceKind = sourceKind,
                    SourceLabel = sourceLabel,
                });
            }
        }

        var rows = new List<RecommendationRow>();
        foreach (var slot in Enum.GetValues<GearSlot>())
        {
            var ordered = buckets[slot].OrderByDescending(x => x.ItemLevel)
                .ThenByDescending(x => x.EquipLevel)
                .ThenByDescending(x => x.Entry.IsHighQuality)
                .ThenByDescending(x => x.Entry.ItemId)
                .ToList();

            var currentlyEquipped = ordered.FirstOrDefault(x => x.SourceKind == ItemSourceKind.EquippedTarget);
            RecommendationCandidate? recommended;
            RecommendationCandidate? reservedBetter = null;

            if (gearsetPolicy == ReservedItemPolicy.Allow)
            {
                recommended = ordered.FirstOrDefault();
            }
            else
            {
                recommended = ordered.FirstOrDefault(x => !x.ReservedByGearSet || x.SourceKind == ItemSourceKind.EquippedTarget);
                if (gearsetPolicy == ReservedItemPolicy.ShowOnly)
                {
                    var bestReserved = ordered.FirstOrDefault(x => x.ReservedByGearSet && x.SourceKind != ItemSourceKind.EquippedTarget);
                    if (bestReserved != null && (recommended == null || Compare(bestReserved, recommended) > 0))
                        reservedBetter = bestReserved;
                }
            }

            rows.Add(new RecommendationRow
            {
                Slot = slot,
                Recommended = recommended,
                CurrentlyEquipped = currentlyEquipped,
                BetterReservedCandidate = reservedBetter,
            });
        }

        // A physical ring cannot fill both ring slots at once. Keep the next distinct instance for the second slot.
        var left = rows.First(x => x.Slot == GearSlot.RingLeft);
        var rightIndex = rows.FindIndex(x => x.Slot == GearSlot.RingRight);
        if (left.Recommended != null && rightIndex >= 0)
        {
            var rightOrdered = buckets[GearSlot.RingRight].OrderByDescending(x => x.ItemLevel)
                .ThenByDescending(x => x.EquipLevel)
                .ThenByDescending(x => x.Entry.IsHighQuality)
                .ThenByDescending(x => x.Entry.ItemId)
                .Where(x => !SamePhysicalItem(x.Entry, left.Recommended.Entry));

            if (gearsetPolicy != ReservedItemPolicy.Allow)
                rightOrdered = rightOrdered.Where(x => !x.ReservedByGearSet || x.SourceKind == ItemSourceKind.EquippedTarget);

            rows[rightIndex] = new RecommendationRow
            {
                Slot = GearSlot.RingRight,
                Recommended = rightOrdered.FirstOrDefault(),
                CurrentlyEquipped = rows[rightIndex].CurrentlyEquipped,
                BetterReservedCandidate = rows[rightIndex].BetterReservedCandidate,
            };
        }

        return rows;
    }

    private bool TryBuildCandidateBase(
        GearTarget target,
        InventoryEntry entry,
        IReadOnlyDictionary<ulong, RetainerInfo> retainerMap,
        out Item item,
        out ItemSourceKind sourceKind,
        out string sourceLabel)
    {
        item = default;
        sourceKind = ItemSourceKind.Unknown;
        sourceLabel = "Unknown";

        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (!itemSheet.TryGetRow(entry.ItemId, out item))
            return false;
        if (item.LevelEquip > target.Level || item.EquipSlotCategory.RowId == 0 || item.ClassJobCategory.RowId == 0)
            return false;
        if (!CanEquip(item.ClassJobCategory.RowId, target.JobId))
            return false;

        sourceKind = GetSourceKind(target, entry);
        if (!SourceEnabled(sourceKind))
            return false;
        if (sourceKind == ItemSourceKind.EquippedElsewhere && !config.IncludeEquippedElsewhere)
            return false;

        sourceLabel = GetSourceLabel(entry, sourceKind, retainerMap);
        return true;
    }

    private bool CanEquip(uint categoryId, uint jobId)
    {
        var key = (categoryId, jobId);
        if (jobCompatibilityCache.TryGetValue(key, out var cached))
            return cached;

        var jobSheet = dataManager.GetExcelSheet<ClassJob>();
        var categorySheet = dataManager.GetExcelSheet<ClassJobCategory>();
        if (!jobSheet.TryGetRow(jobId, out var job) || !categorySheet.TryGetRow(categoryId, out var category))
            return jobCompatibilityCache[key] = false;

        var abbreviation = job.Abbreviation.ToString();
        var property = typeof(ClassJobCategory).GetProperty(abbreviation, BindingFlags.Public | BindingFlags.Instance);
        if (property == null)
            return jobCompatibilityCache[key] = false;

        var value = property.GetValue(category);
        return jobCompatibilityCache[key] = IsTruthy(value);
    }

    private IReadOnlyList<GearSlot> GetSlots(Item item)
    {
        var sheet = dataManager.GetExcelSheet<EquipSlotCategory>();
        if (!sheet.TryGetRow(item.EquipSlotCategory.RowId, out var category))
            return Array.Empty<GearSlot>();

        var slots = new List<GearSlot>();
        AddIf(category, "MainHand", GearSlot.MainHand, slots);
        AddIf(category, "OffHand", GearSlot.OffHand, slots);
        AddIf(category, "Head", GearSlot.Head, slots);
        AddIf(category, "Body", GearSlot.Body, slots);
        AddIf(category, "Gloves", GearSlot.Hands, slots);
        AddIf(category, "Legs", GearSlot.Legs, slots);
        AddIf(category, "Feet", GearSlot.Feet, slots);
        AddIf(category, "Ears", GearSlot.Ears, slots);
        AddIf(category, "Neck", GearSlot.Neck, slots);
        AddIf(category, "Wrists", GearSlot.Wrists, slots);
        AddIf(category, "FingerL", GearSlot.RingLeft, slots);
        AddIf(category, "FingerR", GearSlot.RingRight, slots);
        return slots;
    }

    private static void AddIf(EquipSlotCategory category, string propertyName, GearSlot slot, List<GearSlot> output)
    {
        var property = typeof(EquipSlotCategory).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property != null && IsTruthy(property.GetValue(category)))
            output.Add(slot);
    }

    private static ItemSourceKind GetSourceKind(GearTarget target, InventoryEntry entry)
    {
        // Allagan Tools exposes both the physical inventory container and a sorted/display container.
        // The physical container is authoritative; the sorted value is only a compatibility fallback.
        var kind = ClassifyContainer(target, entry, entry.Container);
        return kind != ItemSourceKind.Unknown
            ? kind
            : ClassifyContainer(target, entry, entry.SortedContainer);
    }

    private static ItemSourceKind ClassifyContainer(GearTarget target, InventoryEntry entry, uint container)
    {
        if (IsEquippedContainer(container))
            return entry.CharacterId == target.Id ? ItemSourceKind.EquippedTarget : ItemSourceKind.EquippedElsewhere;

        if (container <= 3)
            return ItemSourceKind.PlayerInventory;
        if (IsArmouryContainer(container))
            return ItemSourceKind.Armoury;
        if (container is >= 4000 and <= 4101)
            return ItemSourceKind.Chocobo;
        if (container == 2500)
            return ItemSourceKind.Armoire;
        if (container == 2501)
            return ItemSourceKind.GlamourDresser;
        if (container is >= 10000 and <= 10006)
            return ItemSourceKind.Retainer;

        return ItemSourceKind.Unknown;
    }

    private bool SourceEnabled(ItemSourceKind kind) => kind switch
    {
        ItemSourceKind.EquippedTarget => true,
        ItemSourceKind.PlayerInventory => config.IncludePlayerInventory,
        ItemSourceKind.Armoury => config.IncludeArmoury,
        ItemSourceKind.Chocobo => config.IncludeChocobo,
        ItemSourceKind.Retainer => config.IncludeRetainers,
        ItemSourceKind.GlamourDresser => config.IncludeGlamourDresser,
        ItemSourceKind.Armoire => config.IncludeArmoire,
        ItemSourceKind.EquippedElsewhere => true,
        _ => false,
    };

    private static string GetSourceLabel(InventoryEntry entry, ItemSourceKind kind, IReadOnlyDictionary<ulong, RetainerInfo> retainerMap)
    {
        return kind switch
        {
            ItemSourceKind.EquippedTarget => "Equipped",
            ItemSourceKind.PlayerInventory => "Inventory",
            ItemSourceKind.Armoury => "Armoury Chest",
            ItemSourceKind.Chocobo => "Chocobo saddlebag",
            ItemSourceKind.GlamourDresser => "Glamour dresser",
            ItemSourceKind.Armoire => "Armoire",
            ItemSourceKind.Retainer => retainerMap.TryGetValue(entry.CharacterId, out var r) ? $"Retainer: {r.Name}" : $"Retainer {entry.CharacterId}",
            ItemSourceKind.EquippedElsewhere when retainerMap.TryGetValue(entry.CharacterId, out var r) => $"Equipped by {r.Name}",
            ItemSourceKind.EquippedElsewhere => "Equipped elsewhere",
            _ => "Unknown",
        };
    }

    public static bool IsEquippedContainer(uint container) => container is 1000 or 1001 or 11000;
    public static bool IsArmouryContainer(uint container) => container is >= 3200 and <= 3500;
    public static bool IsAccessiblePlayerContainer(uint container) => container <= 3 || IsArmouryContainer(container);
    public static bool IsEquipped(InventoryEntry entry) => IsEquippedContainer(entry.Container) || IsEquippedContainer(entry.SortedContainer);
    public static bool IsAccessiblePlayerItem(InventoryEntry entry) => IsAccessiblePlayerContainer(entry.Container) || IsAccessiblePlayerContainer(entry.SortedContainer);
    public static bool IsArmouryItem(InventoryEntry entry) => IsArmouryContainer(entry.Container) || IsArmouryContainer(entry.SortedContainer);

    private static bool SamePhysicalItem(InventoryEntry a, InventoryEntry b) =>
        a.CharacterId == b.CharacterId && a.Container == b.Container && a.Slot == b.Slot && a.ItemId == b.ItemId;

    private static int Compare(RecommendationCandidate a, RecommendationCandidate b)
    {
        var itemLevel = a.ItemLevel.CompareTo(b.ItemLevel);
        if (itemLevel != 0) return itemLevel;
        var equipLevel = a.EquipLevel.CompareTo(b.EquipLevel);
        if (equipLevel != 0) return equipLevel;
        return a.Entry.IsHighQuality.CompareTo(b.Entry.IsHighQuality);
    }

    private static bool IsTruthy(object? value) => value switch
    {
        bool b => b,
        byte b => b != 0,
        sbyte b => b != 0,
        short b => b != 0,
        ushort b => b != 0,
        int b => b != 0,
        uint b => b != 0,
        long b => b != 0,
        ulong b => b != 0,
        _ => false,
    };
}
