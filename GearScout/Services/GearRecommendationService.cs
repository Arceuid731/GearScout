using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace GearScout.Services;

public sealed class GearRecommendationService
{
    private const uint Strength = 1;
    private const uint Dexterity = 2;
    private const uint Vitality = 3;
    private const uint Intelligence = 4;
    private const uint Mind = 5;
    private const uint Piety = 6;
    private const uint GatheringPoints = 10;
    private const uint CraftingPoints = 11;
    private const uint PhysicalDamage = 12;
    private const uint MagicDamage = 13;
    private const uint BlockRate = 17;
    private const uint BlockStrength = 18;
    private const uint Tenacity = 19;
    private const uint Defense = 21;
    private const uint DirectHit = 22;
    private const uint MagicDefense = 24;
    private const uint CriticalHit = 27;
    private const uint Determination = 44;
    private const uint SkillSpeed = 45;
    private const uint SpellSpeed = 46;
    private const uint Craftsmanship = 70;
    private const uint Control = 71;
    private const uint Gathering = 72;
    private const uint Perception = 73;

    private readonly IDataManager dataManager;
    private readonly Configuration config;
    private readonly AllaganToolsService allagan;
    private readonly RetainerService retainers;
    private readonly Dictionary<(uint CategoryId, uint JobId), bool> jobCompatibilityCache = new();
    private readonly Dictionary<uint, JobProfile> jobProfileCache = new();

    private enum ProfileKind
    {
        Unknown,
        Combat,
        Healer,
        Tank,
        Gatherer,
        Crafter,
    }

    private readonly record struct JobProfile(ProfileKind Kind, uint MainStat, bool UsesMagicDamage, string MainStatName);

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
        var gearsetPolicy = target.IsRetainer ? config.GearsetItems : ReservedItemPolicy.Allow;
        var profile = GetJobProfile(target.JobId);

        foreach (var entry in inventory)
        {
            if (!TryBuildCandidateBase(target, entry, retainerMap, out var item, out var sourceKind, out var sourceLabel))
                continue;

            var slots = GetSlots(item);
            foreach (var slot in slots)
            {
                var score = BuildScore(item, entry.IsHighQuality, profile, slot, out var rankingSummary);
                buckets[slot].Add(new RecommendationCandidate
                {
                    Entry = entry,
                    ItemName = item.Name.ToString(),
                    Slot = slot,
                    ItemLevel = item.LevelItem.RowId,
                    EquipLevel = item.LevelEquip,
                    SourceKind = sourceKind,
                    SourceLabel = sourceLabel,
                    Score = score,
                    RankingSummary = rankingSummary,
                });
            }
        }

        var rows = new List<RecommendationRow>();
        foreach (var slot in Enum.GetValues<GearSlot>())
        {
            var ordered = OrderCandidates(buckets[slot]).ToList();
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
            var rightOrdered = OrderCandidates(buckets[GearSlot.RingRight])
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

    public static bool IsBetter(RecommendationCandidate candidate, RecommendationCandidate current) => Compare(candidate, current) > 0;

    private static IOrderedEnumerable<RecommendationCandidate> OrderCandidates(IEnumerable<RecommendationCandidate> candidates) =>
        candidates.OrderByDescending(x => x.Score.Priority1)
            .ThenByDescending(x => x.Score.Priority2)
            .ThenByDescending(x => x.Score.Priority3)
            .ThenByDescending(x => x.Score.Priority4)
            .ThenByDescending(x => x.ItemLevel)
            .ThenByDescending(x => x.EquipLevel)
            .ThenByDescending(x => x.Entry.IsHighQuality)
            .ThenByDescending(x => x.Entry.ItemId);

    private RecommendationScore BuildScore(Item item, bool highQuality, JobProfile profile, GearSlot slot, out string summary)
    {
        var stats = GetItemStats(item, highQuality);
        long S(uint id) => stats.TryGetValue(id, out var value) ? value : 0;

        switch (profile.Kind)
        {
            case ProfileKind.Crafter:
            {
                var craftsmanship = S(Craftsmanship);
                var control = S(Control);
                var cp = S(CraftingPoints);
                var balancedCore = Math.Min(craftsmanship, control);
                var totalCore = craftsmanship + control;

                summary = $"Rank: balanced crafting stats → total → CP → iLvl | Craftsmanship {craftsmanship} • Control {control} • CP {cp} • iLvl {item.LevelItem.RowId}";
                return new RecommendationScore(balancedCore, totalCore, cp, item.LevelItem.RowId);
            }
            case ProfileKind.Gatherer:
            {
                var gathering = S(Gathering);
                var perception = S(Perception);
                var gp = S(GatheringPoints);
                var balancedCore = Math.Min(gathering, perception);
                var totalCore = gathering + perception;

                summary = $"Rank: balanced gathering stats → total → GP → iLvl | Gathering {gathering} • Perception {perception} • GP {gp} • iLvl {item.LevelItem.RowId}";
                return new RecommendationScore(balancedCore, totalCore, gp, item.LevelItem.RowId);
            }
            case ProfileKind.Tank:
            case ProfileKind.Healer:
            case ProfileKind.Combat:
            {
                var main = S(profile.MainStat);
                var vitality = S(Vitality);
                var defense = S(Defense) + S(MagicDefense);
                var block = S(BlockRate) + S(BlockStrength);
                var secondary = RelevantSecondaryScore(profile.Kind, profile.UsesMagicDamage, S);
                var weaponDamage = profile.UsesMagicDamage ? S(MagicDamage) : S(PhysicalDamage);
                var itemLevel = (long)item.LevelItem.RowId;

                if (slot == GearSlot.MainHand && weaponDamage > 0)
                {
                    summary = $"Rank: weapon damage → {profile.MainStatName} → iLvl → role stats | {(profile.UsesMagicDamage ? "Magic" : "Physical")} Damage {weaponDamage} • {profile.MainStatName} {main} • iLvl {itemLevel} • VIT {vitality}";
                    return new RecommendationScore(weaponDamage, main, itemLevel, vitality + secondary);
                }

                if (profile.Kind == ProfileKind.Tank)
                {
                    if (slot == GearSlot.OffHand && block > 0)
                    {
                        summary = $"Rank: {profile.MainStatName} → iLvl → block → defenses | {profile.MainStatName} {main} • iLvl {itemLevel} • Block {block} • Def {defense} • VIT {vitality}";
                        return new RecommendationScore(main, itemLevel, block, defense + vitality + secondary);
                    }

                    summary = $"Rank: {profile.MainStatName} → iLvl → defenses → VIT/secondaries | {profile.MainStatName} {main} • iLvl {itemLevel} • Def {defense} • VIT {vitality}";
                    return new RecommendationScore(main, itemLevel, defense, vitality + secondary);
                }

                summary = $"Rank: {profile.MainStatName} → iLvl → VIT → role secondaries | {profile.MainStatName} {main} • iLvl {itemLevel} • VIT {vitality} • Secondary {secondary}";
                return new RecommendationScore(main, itemLevel, vitality, secondary);
            }
            default:
                summary = $"Rank: iLvl → equip level → HQ | iLvl {item.LevelItem.RowId}";
                return new RecommendationScore(item.LevelItem.RowId, item.LevelEquip, highQuality ? 1 : 0, item.RowId);
        }
    }

    private static long RelevantSecondaryScore(ProfileKind kind, bool usesMagicDamage, Func<uint, long> stat)
    {
        var crit = stat(CriticalHit);
        var det = stat(Determination);

        return kind switch
        {
            ProfileKind.Tank => crit + det + stat(Tenacity) + stat(SkillSpeed) + stat(DirectHit),
            ProfileKind.Healer => crit + det + stat(SpellSpeed) + stat(Piety),
            ProfileKind.Combat when usesMagicDamage => crit + det + stat(DirectHit) + stat(SpellSpeed),
            ProfileKind.Combat => crit + det + stat(DirectHit) + stat(SkillSpeed),
            _ => 0,
        };
    }

    private static Dictionary<uint, long> GetItemStats(Item item, bool highQuality)
    {
        var result = new Dictionary<uint, long>();

        static void Add(Dictionary<uint, long> target, uint statId, long value)
        {
            if (statId == 0 || value == 0)
                return;
            target[statId] = target.GetValueOrDefault(statId) + value;
        }

        for (var i = 0; i < item.BaseParam.Count; i++)
            Add(result, item.BaseParam[i].RowId, item.BaseParamValue[i]);

        if (highQuality)
        {
            for (var i = 0; i < item.BaseParamSpecial.Count; i++)
                Add(result, item.BaseParamSpecial[i].RowId, item.BaseParamValueSpecial[i]);
        }

        return result;
    }

    private JobProfile GetJobProfile(uint jobId)
    {
        if (jobProfileCache.TryGetValue(jobId, out var cached))
            return cached;

        var sheet = dataManager.GetExcelSheet<ClassJob>(ClientLanguage.English);
        if (!sheet.TryGetRow(jobId, out var job))
            return jobProfileCache[jobId] = new JobProfile(ProfileKind.Unknown, 0, false, "Main");

        var abbreviation = job.Abbreviation.ToString().ToUpperInvariant();
        var profile = abbreviation switch
        {
            "CRP" or "BSM" or "ARM" or "GSM" or "LTW" or "WVR" or "ALC" or "CUL"
                => new JobProfile(ProfileKind.Crafter, 0, false, ""),
            "MIN" or "BTN" or "FSH"
                => new JobProfile(ProfileKind.Gatherer, 0, false, ""),
            "GLA" or "PLD" or "MRD" or "WAR" or "DRK" or "GNB"
                => new JobProfile(ProfileKind.Tank, Strength, false, "STR"),
            "PGL" or "MNK" or "LNC" or "DRG" or "SAM" or "RPR"
                => new JobProfile(ProfileKind.Combat, Strength, false, "STR"),
            "ROG" or "NIN" or "VPR" or "ARC" or "BRD" or "MCH" or "DNC"
                => new JobProfile(ProfileKind.Combat, Dexterity, false, "DEX"),
            "THM" or "BLM" or "ACN" or "SMN" or "RDM" or "BLU" or "PCT"
                => new JobProfile(ProfileKind.Combat, Intelligence, true, "INT"),
            "CNJ" or "WHM" or "SCH" or "AST" or "SGE"
                => new JobProfile(ProfileKind.Healer, Mind, true, "MND"),
            _ => new JobProfile(ProfileKind.Unknown, 0, false, "Main"),
        };

        return jobProfileCache[jobId] = profile;
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

        var jobSheet = dataManager.GetExcelSheet<ClassJob>(ClientLanguage.English);
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

    private static void AddIfPositive(EquipSlotCategory category, string propertyName, GearSlot slot, List<GearSlot> output)
    {
        var property = typeof(EquipSlotCategory).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property != null && IsPositive(property.GetValue(category)))
            output.Add(slot);
    }

    private static ItemSourceKind GetSourceKind(GearTarget target, InventoryEntry entry)
    {
        var kind = ClassifyContainer(target, entry, entry.SortedContainer);
        return kind != ItemSourceKind.Unknown
            ? kind
            : ClassifyContainer(target, entry, entry.Container);
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

    private static bool IsKnownTrackedContainer(uint container) =>
        IsEquippedContainer(container)
        || container <= 3
        || IsArmouryContainer(container)
        || container is >= 4000 and <= 4101
        || container is 2500 or 2501
        || container is >= 10000 and <= 10006;

    public static uint GetLogicalContainer(InventoryEntry entry) =>
        IsKnownTrackedContainer(entry.SortedContainer) ? entry.SortedContainer : entry.Container;

    public static bool IsEquipped(InventoryEntry entry) => IsEquippedContainer(GetLogicalContainer(entry));
    public static bool IsAccessiblePlayerItem(InventoryEntry entry) => IsAccessiblePlayerContainer(GetLogicalContainer(entry));
    public static bool IsArmouryItem(InventoryEntry entry) => IsArmouryContainer(GetLogicalContainer(entry));

    private static bool SamePhysicalItem(InventoryEntry a, InventoryEntry b) =>
        a.CharacterId == b.CharacterId && a.Container == b.Container && a.Slot == b.Slot && a.ItemId == b.ItemId;

    private static int Compare(RecommendationCandidate a, RecommendationCandidate b)
    {
        var value = a.Score.Priority1.CompareTo(b.Score.Priority1);
        if (value != 0) return value;
        value = a.Score.Priority2.CompareTo(b.Score.Priority2);
        if (value != 0) return value;
        value = a.Score.Priority3.CompareTo(b.Score.Priority3);
        if (value != 0) return value;
        value = a.Score.Priority4.CompareTo(b.Score.Priority4);
        if (value != 0) return value;
        value = a.ItemLevel.CompareTo(b.ItemLevel);
        if (value != 0) return value;
        value = a.EquipLevel.CompareTo(b.EquipLevel);
        if (value != 0) return value;
        return a.Entry.IsHighQuality.CompareTo(b.Entry.IsHighQuality);
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
