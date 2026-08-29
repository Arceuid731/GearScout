using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace GearScout.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private IReadOnlyList<InventoryEntry> inventory = Array.Empty<InventoryEntry>();
    private IReadOnlyList<RecommendationRow> recommendations = Array.Empty<RecommendationRow>();
    private DateTime nextRefreshUtc = DateTime.MinValue;
    private string targetKey = string.Empty;
    private bool forceRefresh = true;
    private bool groupPlanBySource = true;
    private Vector2? nativeWindowPosition;
    private float nativeWindowWidth;
    private Vector2 lastKnownSize = new(640, 380);

    public MainWindow(Plugin plugin)
        : base("GearScout##GearScoutMain")
    {
        this.plugin = plugin;
        ForceMainWindow = true;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 380),
            MaximumSize = new Vector2(1200, 1000),
        };
    }

    public void Dispose() { }
    public void RequestRefresh() => forceRefresh = true;

    public void AnchorTo(Vector2 nativePosition, float nativeScaledWidth)
    {
        if (!plugin.Configuration.AnchorToEquipmentWindow)
        {
            ReleaseAnchor();
            return;
        }

        nativeWindowPosition = nativePosition;
        nativeWindowWidth = nativeScaledWidth;
    }

    public void ReleaseAnchor()
    {
        nativeWindowPosition = null;
        Position = null;
        PositionCondition = ImGuiCond.None;
    }

    public override void PreDraw()
    {
        if (!plugin.Configuration.AnchorToEquipmentWindow || nativeWindowPosition == null)
            return;

        const float gap = 10f;
        var viewport = ImGuiHelpers.MainViewport;
        var availableWidth = viewport.WorkSize.X;
        var desiredWidth = Math.Max(lastKnownSize.X, SizeConstraints?.MinimumSize.X * ImGuiHelpers.GlobalScale ?? 640f);

        var right = nativeWindowPosition.Value.X + nativeWindowWidth + gap;
        var left = nativeWindowPosition.Value.X - desiredWidth - gap;
        var x = right + desiredWidth <= availableWidth
            ? right
            : Math.Max(0f, left);

        Position = new Vector2(x, Math.Max(0f, nativeWindowPosition.Value.Y));
        PositionCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        lastKnownSize = ImGui.GetWindowSize();

        if (!plugin.AllaganTools.IsAvailable)
        {
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), "Allagan Tools is not available.");
            ImGui.TextWrapped("GearScout uses Allagan Tools as its global inventory index. Install/enable Allagan Tools, then open the storages you want it to snapshot.");
            if (ImGui.Button("Settings"))
                plugin.ToggleConfigUi();
            return;
        }

        var target = plugin.GetEffectiveTarget();
        if (target == null)
        {
            ImGui.TextWrapped("No equipment target is available. Open your Character window or a retainer's equipment window.");
            return;
        }

        if (forceRefresh || target.Key != targetKey || DateTime.UtcNow >= nextRefreshUtc)
            Refresh(target);

        DrawHeader(target);

        if (plugin.Configuration.ActivePlan != null)
        {
            if (ImGui.BeginTabBar("##GearScoutTabs"))
            {
                if (ImGui.BeginTabItem("Gear plan"))
                {
                    DrawPlan(target);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Recommendations"))
                {
                    DrawRecommendations(target);
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }
        else
        {
            DrawRecommendations(target);
        }
    }

    private void Refresh(GearTarget target)
    {
        inventory = plugin.AllaganTools.GetAllItems();
        plugin.PlanService.Refresh(inventory);
        recommendations = plugin.RecommendationService.Build(target, inventory);
        targetKey = target.Key;
        nextRefreshUtc = DateTime.UtcNow.AddSeconds(1);
        forceRefresh = false;
    }

    private void DrawHeader(GearTarget target)
    {
        var job = GetJobAbbreviation(target.JobId);
        ImGui.TextUnformatted(target.IsRetainer ? $"Retainer: {target.Name}" : target.Name);
        ImGui.SameLine();
        ImGui.TextDisabled($"{job} Lv. {target.Level}");

        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh"))
            RequestRefresh();
        ImGui.SameLine();
        if (ImGui.SmallButton("Settings"))
            plugin.ToggleConfigUi();

        var currentItems = recommendations.Select(x => x.CurrentlyEquipped).Where(x => x != null).Cast<RecommendationCandidate>().ToList();
        var recommendedItems = recommendations.Select(x => x.Recommended).Where(x => x != null).Cast<RecommendationCandidate>().ToList();
        if (currentItems.Count > 0 && recommendedItems.Count > 0)
        {
            var currentAverage = currentItems.Average(x => x.ItemLevel);
            var recommendedAverage = recommendedItems.Average(x => x.ItemLevel);
            ImGui.TextDisabled($"Owned recommendation: avg iLvl {recommendedAverage:0}   |   currently detected: {currentAverage:0}");
        }
        else
        {
            ImGui.TextDisabled("Recommendations are based on equippable level + item level, mirroring the game's Recommended Gear philosophy rather than BiS stat weights.");
        }

        ImGui.Separator();
    }

    private void DrawRecommendations(GearTarget target)
    {
        ImGui.TextWrapped("Select the pieces you actually want for this target. The selection becomes a persistent plan and stays visible while you retrieve the items.");
        ImGui.Spacing();

        if (!ImGui.BeginTable("##Recommendations", 6,
                ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
                new Vector2(0, -1)))
            return;

        ImGui.TableSetupColumn("Plan", ImGuiTableColumnFlags.WidthFixed, 42);
        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Recommended", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("iLvl", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("Location", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Notes", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var row in recommendations)
        {
            var candidate = row.Recommended;
            if (candidate == null)
                continue;

            var current = row.CurrentlyEquipped;
            var isUpgrade = current == null || candidate.ItemLevel > current.ItemLevel || candidate.Entry.Fingerprint != current.Entry.Fingerprint;
            if (plugin.Configuration.ShowOnlyUpgrades && !isUpgrade && row.BetterReservedCandidate == null)
                continue;

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            var selected = plugin.PlanService.IsSelected(target, candidate);
            var previous = selected;
            ImGui.Checkbox($"##plan-{row.Slot}", ref selected);
            if (selected != previous)
            {
                plugin.PlanService.ToggleSelection(target, candidate);
                RequestRefresh();
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(SlotName(row.Slot));

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(candidate.ItemName + (candidate.Entry.IsHighQuality ? " HQ" : string.Empty));
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(candidate.ItemName);
                ImGui.TextDisabled($"Equip level {candidate.EquipLevel} • item level {candidate.ItemLevel}");
                ImGui.TextDisabled($"Item #{candidate.Entry.ItemId} • {candidate.SourceLabel}");
                if (candidate.Entry.InGearSet)
                    ImGui.TextColored(new Vector4(1f, 0.75f, 0.25f, 1f), $"Used by gearset(s): {string.Join(", ", candidate.Entry.GearSets.Select(x => $"#{x + 1}"))}");
                ImGui.EndTooltip();
            }

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(candidate.ItemLevel.ToString());

            ImGui.TableSetColumnIndex(4);
            DrawSource(candidate);

            ImGui.TableSetColumnIndex(5);
            if (candidate.SourceKind == ItemSourceKind.EquippedTarget)
                ImGui.TextColored(new Vector4(0.35f, 0.9f, 0.45f, 1f), "Already equipped");
            else if (current != null)
                ImGui.TextDisabled($"Current: {current.ItemName} ({current.ItemLevel})");
            else
                ImGui.TextDisabled("No equipped piece detected");

            if (row.BetterReservedCandidate != null)
            {
                ImGui.TextColored(new Vector4(1f, 0.75f, 0.25f, 1f),
                    $"Reserved better: {row.BetterReservedCandidate.ItemName} ({row.BetterReservedCandidate.ItemLevel})");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{row.BetterReservedCandidate.SourceLabel}\nUsed by one or more gearsets; current policy is Show as reserved.");
            }
        }

        ImGui.EndTable();
    }

    private void DrawPlan(GearTarget target)
    {
        var plan = plugin.Configuration.ActivePlan;
        if (plan == null)
            return;

        var total = plan.Items.Count;
        var retrieved = plan.Items.Count(x => x.State is PlanItemState.Retrieved or PlanItemState.Equipped);
        var equipped = plan.Items.Count(x => x.State == PlanItemState.Equipped);
        ImGui.Text($"{retrieved}/{total} retrieved   •   {equipped}/{total} equipped");

        if (ImGui.Button("Recalculate selected slots"))
        {
            plugin.PlanService.ReplaceSelectedRecommendations(target, recommendations);
            RequestRefresh();
        }
        ImGui.SameLine();
        if (ImGui.Button("Finish / clear plan"))
        {
            plugin.PlanService.Clear();
            RequestRefresh();
            return;
        }
        ImGui.SameLine();
        ImGui.Checkbox("Group by location", ref groupPlanBySource);

        ImGui.Spacing();
        if (groupPlanBySource)
        {
            var groups = plan.Items
                .OrderBy(x => x.State)
                .ThenBy(x => x.CurrentSourceLabel)
                .GroupBy(x => PlanGroup(x));

            foreach (var group in groups)
            {
                if (ImGui.CollapsingHeader($"{group.Key} ({group.Count()})", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    foreach (var item in group)
                        DrawPlanItem(item);
                }
            }
        }
        else
        {
            foreach (var item in plan.Items.OrderBy(x => x.Slot))
                DrawPlanItem(item);
        }
    }

    private static string PlanGroup(PlannedGearItem item) => item.State switch
    {
        PlanItemState.Equipped => "Equipped",
        PlanItemState.Retrieved => "Ready to equip (Inventory / Armoury)",
        PlanItemState.Missing => "Location unknown",
        _ => item.CurrentSourceLabel,
    };

    private static void DrawPlanItem(PlannedGearItem item)
    {
        var color = item.State switch
        {
            PlanItemState.ToRetrieve => new Vector4(1f, 0.35f, 0.3f, 1f),
            PlanItemState.Retrieved => new Vector4(0.3f, 0.95f, 0.4f, 1f),
            PlanItemState.Equipped => new Vector4(0.35f, 0.9f, 0.85f, 1f),
            _ => new Vector4(1f, 0.7f, 0.25f, 1f),
        };
        var state = item.State switch
        {
            PlanItemState.ToRetrieve => "TO RETRIEVE",
            PlanItemState.Retrieved => "RETRIEVED",
            PlanItemState.Equipped => "EQUIPPED",
            _ => "UNKNOWN",
        };

        ImGui.TextColored(color, state);
        ImGui.SameLine(125);
        ImGui.TextUnformatted($"{SlotName(item.Slot),-10}  {item.ItemName}  (iLvl {item.ItemLevel})");
        ImGui.SameLine();
        ImGui.TextDisabled($"— {item.CurrentSourceLabel}");
    }

    private static void DrawSource(RecommendationCandidate candidate)
    {
        var color = candidate.SourceKind switch
        {
            ItemSourceKind.EquippedTarget => new Vector4(0.35f, 0.9f, 0.45f, 1f),
            ItemSourceKind.PlayerInventory or ItemSourceKind.Armoury => new Vector4(0.45f, 0.85f, 1f, 1f),
            ItemSourceKind.EquippedElsewhere => new Vector4(1f, 0.65f, 0.25f, 1f),
            _ => Vector4.One,
        };
        ImGui.TextColored(color, candidate.SourceLabel);
        if (candidate.Entry.InGearSet)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.25f, 1f), "[gearset]");
        }
    }

    private string GetJobAbbreviation(uint jobId)
    {
        return plugin.DataManager.GetExcelSheet<ClassJob>().TryGetRow(jobId, out var job)
            ? job.Abbreviation.ToString()
            : $"Job {jobId}";
    }

    private static string SlotName(GearSlot slot) => slot switch
    {
        GearSlot.MainHand => "Weapon",
        GearSlot.OffHand => "Off hand",
        GearSlot.Head => "Head",
        GearSlot.Body => "Body",
        GearSlot.Hands => "Hands",
        GearSlot.Legs => "Legs",
        GearSlot.Feet => "Feet",
        GearSlot.Ears => "Earrings",
        GearSlot.Neck => "Necklace",
        GearSlot.Wrists => "Bracelet",
        GearSlot.RingLeft => "Ring 1",
        GearSlot.RingRight => "Ring 2",
        _ => slot.ToString(),
    };
}
