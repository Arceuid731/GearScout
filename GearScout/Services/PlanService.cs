using System;
using System.Collections.Generic;
using System.Linq;

namespace GearScout.Services;

public sealed class PlanService
{
    private readonly Configuration config;
    private readonly AllaganToolsService allagan;

    public PlanService(Configuration config, AllaganToolsService allagan)
    {
        this.config = config;
        this.allagan = allagan;
    }

    public GearPlan? ActivePlan => config.ActivePlan;

    public bool IsSelected(GearTarget target, RecommendationCandidate candidate)
    {
        var plan = config.ActivePlan;
        if (plan == null || plan.TargetKey != target.Key)
            return false;

        return plan.Items.Any(x => x.Slot == candidate.Slot && x.Fingerprint == candidate.Entry.Fingerprint);
    }

    public bool ToggleSelection(GearTarget target, RecommendationCandidate candidate)
    {
        EnsurePlan(target);
        var plan = config.ActivePlan!;

        var existing = plan.Items.FirstOrDefault(x => x.Slot == candidate.Slot);
        if (existing != null && existing.Fingerprint == candidate.Entry.Fingerprint)
        {
            plan.Items.Remove(existing);
            if (plan.Items.Count == 0)
                config.ActivePlan = null;
            config.Save();
            return false;
        }

        if (existing != null)
            plan.Items.Remove(existing);

        plan.Items.Add(ToPlannedItem(candidate));
        config.Save();
        return true;
    }

    public void ReplaceSelectedRecommendations(GearTarget target, IReadOnlyList<RecommendationRow> rows)
    {
        var plan = config.ActivePlan;
        if (plan == null || plan.TargetKey != target.Key)
            return;

        var selectedSlots = plan.Items.Select(x => x.Slot).ToHashSet();
        foreach (var row in rows.Where(x => selectedSlots.Contains(x.Slot) && x.Recommended != null))
        {
            plan.Items.RemoveAll(x => x.Slot == row.Slot);
            plan.Items.Add(ToPlannedItem(row.Recommended!));
        }

        config.Save();
    }

    public void Refresh(IReadOnlyList<InventoryEntry>? inventory = null)
    {
        var plan = config.ActivePlan;
        if (plan == null)
            return;

        inventory ??= allagan.GetAllItems();
        var activeCharacterId = allagan.CurrentCharacterId;

        foreach (var planned in plan.Items)
        {
            var exact = inventory.Where(x => x.Fingerprint == planned.Fingerprint).ToList();
            if (exact.Count == 0)
                exact = inventory.Where(x => x.ItemId == planned.ItemId && x.Flags == planned.Flags).ToList();

            var equipped = exact.FirstOrDefault(x =>
                x.CharacterId == plan.TargetId && GearRecommendationService.IsEquippedContainer(x.SortedContainer));
            if (equipped != null)
            {
                planned.State = PlanItemState.Equipped;
                planned.CurrentSourceLabel = "Equipped";
                continue;
            }

            var retrieved = exact.FirstOrDefault(x =>
                x.CharacterId == activeCharacterId && GearRecommendationService.IsAccessiblePlayerContainer(x.SortedContainer));
            if (retrieved != null)
            {
                planned.State = PlanItemState.Retrieved;
                planned.CurrentSourceLabel = retrieved.SortedContainer <= 3 ? "Inventory" : "Armoury Chest";
                continue;
            }

            var elsewhere = exact.FirstOrDefault();
            if (elsewhere != null)
            {
                planned.State = PlanItemState.ToRetrieve;
                planned.CurrentSourceLabel = string.IsNullOrWhiteSpace(planned.OriginalSourceLabel)
                    ? "Stored elsewhere"
                    : planned.OriginalSourceLabel;
                continue;
            }

            planned.State = PlanItemState.Missing;
            planned.CurrentSourceLabel = "Location no longer known";
        }
    }

    public void Clear()
    {
        config.ActivePlan = null;
        config.Save();
    }

    public void CompleteIfEquipped()
    {
        var plan = config.ActivePlan;
        if (plan != null && plan.Items.Count > 0 && plan.Items.All(x => x.State == PlanItemState.Equipped))
        {
            config.ActivePlan = null;
            config.Save();
        }
    }

    private void EnsurePlan(GearTarget target)
    {
        if (config.ActivePlan?.TargetKey == target.Key)
            return;

        config.ActivePlan = new GearPlan
        {
            TargetKey = target.Key,
            TargetId = target.Id,
            TargetName = target.Name,
            JobId = target.JobId,
            Level = target.Level,
            IsRetainer = target.IsRetainer,
            CreatedUtc = DateTime.UtcNow,
        };
    }

    private static PlannedGearItem ToPlannedItem(RecommendationCandidate candidate) => new()
    {
        Slot = candidate.Slot,
        ItemId = candidate.Entry.ItemId,
        ItemName = candidate.ItemName,
        Fingerprint = candidate.Entry.Fingerprint,
        Flags = candidate.Entry.Flags,
        ItemLevel = candidate.ItemLevel,
        OriginalCharacterId = candidate.Entry.CharacterId,
        OriginalContainer = candidate.Entry.SortedContainer,
        OriginalSourceLabel = candidate.SourceLabel,
        CurrentSourceLabel = candidate.SourceLabel,
        State = candidate.SourceKind == ItemSourceKind.EquippedTarget
            ? PlanItemState.Equipped
            : candidate.SourceKind is ItemSourceKind.PlayerInventory or ItemSourceKind.Armoury
                ? PlanItemState.Retrieved
                : PlanItemState.ToRetrieve,
    };
}
