using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace GearScout.Services;

public sealed record CofferSuggestion(
    uint ItemId,
    string ItemName,
    uint ItemLevel,
    string SourceLabel,
    IReadOnlyList<GearSlot> PotentialSlots,
    bool IsAttire,
    bool LooksLikeUpgrade);

/// <summary>
/// Discovers unopened gear coffers from native game data only.
/// Exact contents are deliberately never assumed.
/// </summary>
public sealed class CofferSuggestionService
{
    private static readonly Regex ItemLevelRegex = new(
        @"(?:\bIL\b|\biLvl\b|Item\s*Level)\s*[:#]?\s*(\d{2,4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IDataManager dataManager;
    private readonly Configuration configuration;
    private readonly RetainerService retainers;

    public CofferSuggestionService(IDataManager dataManager, Configuration configuration, RetainerService retainers)
    {
        this.dataManager = dataManager;
        this.configuration = configuration;
        this.retainers = retainers;
    }

    public IReadOnlyList<CofferSuggestion> Build(
        GearTarget target,
        IReadOnlyList<InventoryEntry> inventory,
        IReadOnlyList<RecommendationRow> recommendations)
    {
        if (!configuration.IncludeCofferSuggestions)
            return Array.Empty<CofferSuggestion>();

        var localized = dataManager.GetExcelSheet<Item>();
        var english = dataManager.GetExcelSheet<Item>(ClientLanguage.English);
        var retainersById = retainers.GetRetainers();
        var maxEquippableBySlot = BuildTargetItemLevelCeilings(target);
        var bestBySlot = recommendations.ToDictionary(
            x => x.Slot,
            x => x.Recommended?.ItemLevel ?? x.CurrentlyEquipped?.ItemLevel ?? 0u);

        var output = new List<CofferSuggestion>();
        foreach (var entry in inventory)
        {
            if (entry.Quantity == 0 || GearRecommendationService.IsEquipped(entry))
                continue;

            var logical = GearRecommendationService.GetLogicalContainer(entry);
            if (!IsEnabledSource(logical))
                continue;

            if (!english.TryGetRow(entry.ItemId, out var enItem) || !localized.TryGetRow(entry.ItemId, out var displayItem))
                continue;

            if (enItem.EquipSlotCategory.RowId != 0)
                continue;

            var englishName = enItem.Name.ToString();
            if (!LooksLikeGearCoffer(englishName))
                continue;

            var inferredSlots = InferSlots(englishName);
            if (inferredSlots.Count == 0)
                continue;

            // The coffer object itself is often iLvl 1 even when its tooltip/name says
            // that the reward is e.g. IL 630. Prefer that explicit reward iLvl hint.
            var rewardItemLevel = InferRewardItemLevel(enItem, englishName);
            if (rewardItemLevel == 0)
                continue; // Too uncertain to put in the recommendation table.

            // Only keep slots for which an item of this iLvl is actually plausible for
            // the target job at its current level. The ceiling is derived from the native
            // Item/ClassJob sheets, not from a hard-coded level-to-iLvl table.
            var slots = inferredSlots
                .Where(slot => maxEquippableBySlot.TryGetValue(slot, out var ceiling) && ceiling > 0 && rewardItemLevel <= ceiling)
                .ToList();
            if (slots.Count == 0)
                continue;

            var upgrade = slots.Any(slot => rewardItemLevel > bestBySlot.GetValueOrDefault(slot));
            var source = SourceLabel(entry, logical, retainersById);

            output.Add(new CofferSuggestion(
                entry.ItemId,
                displayItem.Name.ToString(),
                rewardItemLevel,
                source,
                slots,
                englishName.Contains("Attire", StringComparison.OrdinalIgnoreCase) || englishName.Contains("Gear Coffer", StringComparison.OrdinalIgnoreCase),
                upgrade));
        }

        return output
            .OrderByDescending(x => x.LooksLikeUpgrade)
            .ThenByDescending(x => x.ItemLevel)
            .ThenBy(x => x.ItemName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private Dictionary<GearSlot, uint> BuildTargetItemLevelCeilings(GearTarget target)
    {
        var result = Enum.GetValues<GearSlot>().ToDictionary(x => x, _ => 0u);
        var items = dataManager.GetExcelSheet<Item>(ClientLanguage.English);
        var slotCategories = dataManager.GetExcelSheet<EquipSlotCategory>();

        foreach (var item in items)
        {
            if (item.LevelEquip > target.Level || item.EquipSlotCategory.RowId == 0 || item.ClassJobCategory.RowId == 0)
                continue;
            if (!CanEquip(item.ClassJobCategory.RowId, target.JobId))
                continue;
            if (!slotCategories.TryGetRow(item.EquipSlotCategory.RowId, out var category))
                continue;

            var ilvl = item.LevelItem.RowId;
            foreach (var slot in GetSlots(category))
            {
                if (ilvl > result[slot])
                    result[slot] = ilvl;
            }
        }

        return result;
    }

    private bool CanEquip(uint categoryId, uint jobId)
    {
        var jobs = dataManager.GetExcelSheet<ClassJob>(ClientLanguage.English);
        var categories = dataManager.GetExcelSheet<ClassJobCategory>();
        if (!jobs.TryGetRow(jobId, out var job) || !categories.TryGetRow(categoryId, out var category))
            return false;

        var property = typeof(ClassJobCategory).GetProperty(job.Abbreviation.ToString(), BindingFlags.Public | BindingFlags.Instance);
        return property != null && IsPositive(property.GetValue(category));
    }

    private static IReadOnlyList<GearSlot> GetSlots(EquipSlotCategory category)
    {
        var slots = new List<GearSlot>();
        AddIfPositive(category, "MainHand", GearSlot.MainHand, slots);
        AddIfPositive(category, "OffHand", GearSlot.OffHand, slots);
        AddIfPositive(category, "Head", GearSlot.Head, slots);
        AddIfPositive(category, "Body", GearSlot.Body, slots);
        AddIfPositive(category, "Gloves", GearSlot.Hands, slots);
        AddIfPositive(category, "Legs", GearSlot.Legs, slots);
        AddIfPositive(category, "Feet", GearSlot.Feet, slots);
        AddIfPositive(category, "Ears", GearSlot.Ears, slots);
        AddIfPositive(category, "Neck", GearSlot.Neck, slots);
        AddIfPositive(category, "Wrists", GearSlot.Wrists, slots);
        AddIfPositive(category, "FingerL", GearSlot.RingLeft, slots);
        AddIfPositive(category, "FingerR", GearSlot.RingRight, slots);
        return slots;
    }

    private static void AddIfPositive(EquipSlotCategory category, string propertyName, GearSlot slot, ICollection<GearSlot> output)
    {
        var property = typeof(EquipSlotCategory).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property != null && IsPositive(property.GetValue(category)))
            output.Add(slot);
    }

    private static bool IsPositive(object? value) => value switch
    {
        bool b => b,
        byte b => b > 0,
        sbyte b => b > 0,
        short b => b > 0,
        ushort b => b > 0,
        int b => b > 0,
        uint b => b > 0,
        long b => b > 0,
        ulong b => b > 0,
        _ => false,
    };

    private static uint InferRewardItemLevel(Item item, string englishName)
    {
        var match = ItemLevelRegex.Match(englishName);
        if (match.Success && uint.TryParse(match.Groups[1].Value, out var parsed) && parsed > 1)
            return parsed;

        // A few coffers may genuinely expose a meaningful LevelItem. Do not use the
        // ubiquitous container iLvl 1 because it describes the box, not its reward.
        return item.LevelItem.RowId > 1 ? item.LevelItem.RowId : 0;
    }

    private bool IsEnabledSource(uint container)
    {
        if (container <= 3) return configuration.IncludePlayerInventory;
        if (GearRecommendationService.IsArmouryContainer(container)) return configuration.IncludeArmoury;
        if (container is >= 4000 and <= 4101) return configuration.IncludeChocobo;
        if (container is >= 10000 and <= 10006) return configuration.IncludeRetainers;
        return false;
    }

    private static bool LooksLikeGearCoffer(string name)
    {
        if (!name.Contains("Coffer", StringComparison.OrdinalIgnoreCase))
            return false;

        if (name.Contains("Materia", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Minion", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Orchestrion", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Dye", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Token", StringComparison.OrdinalIgnoreCase))
            return false;

        return name.Contains("Gear", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Attire", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Armor", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Armour", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Weapon", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Arms", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Head", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Chest", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Body", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Hand", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Leg", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Foot", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Feet", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Earring", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Neck", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Bracelet", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Wrist", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Ring", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<GearSlot> InferSlots(string name)
    {
        var result = new List<GearSlot>();
        void Add(GearSlot slot) { if (!result.Contains(slot)) result.Add(slot); }

        if (ContainsAny(name, "Weapon", " Arms", "Arms Coffer")) Add(GearSlot.MainHand);
        if (ContainsAny(name, "Head Gear", "Headgear", "Head Coffer", "Helm")) Add(GearSlot.Head);
        if (ContainsAny(name, "Chest Gear", "Body Gear", "Chest Coffer", "Body Coffer")) Add(GearSlot.Body);
        if (ContainsAny(name, "Hand Gear", "Hands Gear", "Hand Coffer", "Glove")) Add(GearSlot.Hands);
        if (ContainsAny(name, "Leg Gear", "Legs Gear", "Leg Coffer", "Trouser")) Add(GearSlot.Legs);
        if (ContainsAny(name, "Foot Gear", "Feet Gear", "Foot Coffer", "Shoe", "Boot")) Add(GearSlot.Feet);
        if (name.Contains("Earring", StringComparison.OrdinalIgnoreCase)) Add(GearSlot.Ears);
        if (name.Contains("Neck", StringComparison.OrdinalIgnoreCase)) Add(GearSlot.Neck);
        if (ContainsAny(name, "Bracelet", "Wrist")) Add(GearSlot.Wrists);
        if (name.Contains("Ring", StringComparison.OrdinalIgnoreCase)) { Add(GearSlot.RingLeft); Add(GearSlot.RingRight); }

        if (result.Count == 0 && ContainsAny(name, "Attire Coffer", "Gear Coffer", "Armor Coffer", "Armour Coffer"))
        {
            Add(GearSlot.Head); Add(GearSlot.Body); Add(GearSlot.Hands); Add(GearSlot.Legs); Add(GearSlot.Feet);
        }

        return result;
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase));

    private static string SourceLabel(InventoryEntry entry, uint container, IReadOnlyDictionary<ulong, RetainerInfo> retainerMap)
    {
        if (container <= 3) return "Inventory";
        if (GearRecommendationService.IsArmouryContainer(container)) return "Armoury";
        if (container is >= 4000 and <= 4101) return "Chocobo";
        if (container is >= 10000 and <= 10006)
            return retainerMap.TryGetValue(entry.CharacterId, out var retainer) ? $"Retainer: {retainer.Name}" : "Retainer";
        return "Owned";
    }
}
