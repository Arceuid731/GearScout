using System;
using System.Collections.Generic;
using System.Linq;
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
/// Discovers unopened gear coffers from the native Item sheet only.
///
/// This deliberately does not pretend to know the exact reward when the game data does
/// not expose it directly. The coffer's own item level and English canonical name are
/// used to infer the potential slot(s), then the UI labels the result as "open to reveal".
/// </summary>
public sealed class CofferSuggestionService
{
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

            // A gear coffer is itself not equippable. Use the canonical English name so
            // this is independent from the player's client language.
            if (enItem.EquipSlotCategory.RowId != 0)
                continue;

            var englishName = enItem.Name.ToString();
            if (!LooksLikeGearCoffer(englishName))
                continue;

            var slots = InferSlots(englishName);
            if (slots.Count == 0)
                continue;

            var itemLevel = enItem.LevelItem.RowId;
            // Some cosmetic attire coffers have no meaningful iLvl. Keep them visible as
            // potential gear, but don't call them an upgrade based on a made-up score.
            var upgrade = itemLevel > 0 && slots.Any(slot => itemLevel > bestBySlot.GetValueOrDefault(slot));
            var source = SourceLabel(entry, logical, retainersById);

            output.Add(new CofferSuggestion(
                entry.ItemId,
                displayItem.Name.ToString(),
                itemLevel,
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

        // Avoid obvious non-equipment coffers while keeping future gear naming flexible.
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

        // Generic set/attire coffers normally contain the visible armour set. We do not
        // claim an exact reward; these slots are only the set of things worth checking.
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
