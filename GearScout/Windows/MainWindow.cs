using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using GearScout.Services;
using Lumina.Excel.Sheets;

namespace GearScout.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly CofferSuggestionService cofferService;
    private IReadOnlyList<InventoryEntry> inventory = Array.Empty<InventoryEntry>();
    private IReadOnlyList<RecommendationRow> recommendations = Array.Empty<RecommendationRow>();
    private IReadOnlyList<CofferSuggestion> cofferSuggestions = Array.Empty<CofferSuggestion>();
    private DateTime nextRefreshUtc = DateTime.MinValue;
    private string targetKey = string.Empty;
    private bool forceRefresh = true;
    private Vector2? nativeWindowPosition;
    private float nativeWindowWidth;
    private Vector2 lastKnownSize = new(520, 420);

    private static readonly Vector4 RetrieveColor = new(1f, .58f, .22f, 1f);
    private static readonly Vector4 ReadyColor = new(.35f, .9f, .48f, 1f);
    private static readonly Vector4 EquippedColor = new(.35f, .82f, 1f, 1f);
    private static readonly Vector4 MissingColor = new(1f, .35f, .35f, 1f);
    private static readonly Vector4 PotentialColor = new(1f, .78f, .28f, 1f);

    public MainWindow(Plugin plugin) : base("GearScout##GearScoutMain")
    {
        this.plugin = plugin;
        cofferService = new CofferSuggestionService(plugin.DataManager, plugin.Configuration, plugin.RetainerService);
        ForceMainWindow = true;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(430, 260), MaximumSize = new Vector2(900, 900) };
    }

    public void Dispose() { }
    public void RequestRefresh() => forceRefresh = true;
    private bool DetachedCompact => !plugin.NativeEquipmentWindowOpen && plugin.Configuration.CompactPlanWhenDetached && plugin.Configuration.ActivePlan is { Items.Count: > 0 };

    public void AnchorTo(Vector2 nativePosition, float nativeScaledWidth)
    {
        if (!plugin.Configuration.AnchorToEquipmentWindow) { ReleaseAnchor(); return; }
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
        if (DetachedCompact)
        {
            SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(430, 220), MaximumSize = new Vector2(620, 760) };
            return;
        }

        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(500, 300), MaximumSize = new Vector2(900, 900) };
        if (!plugin.Configuration.AnchorToEquipmentWindow || nativeWindowPosition == null)
            return;

        const float gap = 10f;
        var viewport = ImGuiHelpers.MainViewport;
        var desiredWidth = Math.Max(lastKnownSize.X, 500f * ImGuiHelpers.GlobalScale);
        var workLeft = viewport.WorkPos.X;
        var workRight = viewport.WorkPos.X + viewport.WorkSize.X;
        var left = nativeWindowPosition.Value.X - desiredWidth - gap;
        var right = nativeWindowPosition.Value.X + nativeWindowWidth + gap;

        var x = left >= workLeft
            ? left
            : right + desiredWidth <= workRight
                ? right
                : Math.Clamp(left, workLeft, Math.Max(workLeft, workRight - desiredWidth));

        Position = new Vector2(x, Math.Max(viewport.WorkPos.Y, nativeWindowPosition.Value.Y));
        PositionCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        lastKnownSize = ImGui.GetWindowSize();
        if (!plugin.AllaganTools.IsAvailable)
        {
            ImGui.TextColored(MissingColor, "Allagan Tools is not available.");
            return;
        }

        var target = plugin.GetEffectiveTarget();
        if (target == null)
        {
            ImGui.TextDisabled("Open Character or retainer equipment to start.");
            return;
        }

        if (forceRefresh || target.Key != targetKey || DateTime.UtcNow >= nextRefreshUtc)
            Refresh(target);

        if (DetachedCompact)
        {
            DrawCompactPlan(target);
            return;
        }

        DrawHeader(target);
        if (plugin.Configuration.ActivePlan is { Items.Count: > 0 })
        {
            if (ImGui.BeginTabBar("##tabs"))
            {
                if (ImGui.BeginTabItem("Plan"))
                {
                    DrawCompactPlan(target, false);
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
        cofferSuggestions = cofferService.Build(target, inventory, recommendations);
        targetKey = target.Key;
        nextRefreshUtc = DateTime.UtcNow.AddSeconds(1);
        forceRefresh = false;
    }

    private void DrawHeader(GearTarget target)
    {
        ImGui.TextUnformatted(target.Name);
        ImGui.SameLine();
        ImGui.TextDisabled($"{GetJobAbbreviation(target.JobId)} {target.Level}");
        ImGui.SameLine(ImGui.GetWindowWidth() - 105);
        if (ImGui.SmallButton("↻##refresh")) RequestRefresh();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Refresh");
        ImGui.SameLine();
        if (ImGui.SmallButton("⚙##settings")) plugin.ToggleConfigUi();
        ImGui.Separator();
    }

    private void DrawRecommendations(GearTarget target)
    {
        if (!ImGui.BeginTable("##recommendations", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, -1)))
            return;

        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Recommendation", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthFixed, 125);
        ImGui.TableHeadersRow();

        foreach (var row in recommendations)
        {
            var exact = row.Recommended;
            var current = row.CurrentlyEquipped;
            var potential = GetPotentialCoffers(row.Slot);
            var exactUpgrade = exact != null && (current == null || GearRecommendationService.IsBetter(exact, current));
            var potentialUpgrade = potential.Any(x => IsPotentialUpgradeForSlot(x, exact, current));

            if (exact == null && potential.Count == 0)
                continue;
            if (plugin.Configuration.ShowOnlyUpgrades && !exactUpgrade && row.BetterReservedCandidate == null && !potentialUpgrade)
                continue;

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            if (exact != null)
            {
                var selected = plugin.PlanService.IsSelected(target, exact);
                var old = selected;
                ImGui.Checkbox($"##plan-{row.Slot}", ref selected);
                if (selected != old)
                {
                    plugin.PlanService.ToggleSelection(target, exact);
                    RequestRefresh();
                }
            }
            else
            {
                ImGui.TextColored(PotentialColor, "◇");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Potential recommendation only — open the coffer to reveal the actual item.");
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.TextDisabled(SlotName(row.Slot));

            ImGui.TableSetColumnIndex(2);
            if (exact != null)
                DrawExactRecommendation(exact);
            else
                ImGui.TextDisabled("No exact owned piece found");

            if (potential.Count > 0)
                DrawPotentialRecommendations(row.Slot, potential, exact, current);

            if (row.BetterReservedCandidate != null)
            {
                ImGui.TextColored(PotentialColor, $"Reserved: {row.BetterReservedCandidate.ItemName}  iLvl {row.BetterReservedCandidate.ItemLevel}");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{row.BetterReservedCandidate.SourceLabel}\nUsed by one or more gearsets; current policy is Show as reserved.");
            }

            ImGui.TableSetColumnIndex(3);
            if (exact != null)
                ImGui.TextColored(SourceColor(exact.SourceKind), ShortSource(exact.SourceLabel));
            else if (potential.Count > 0)
                ImGui.TextColored(PotentialColor, ShortSource(potential[0].SourceLabel));
        }

        ImGui.EndTable();
    }

    private void DrawExactRecommendation(RecommendationCandidate candidate)
    {
        DrawItemIcon(candidate.Entry.ItemId, 24);
        ImGui.SameLine();
        ImGui.TextUnformatted(candidate.ItemName + (candidate.Entry.IsHighQuality ? " HQ" : ""));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Exact owned item\niLvl {candidate.ItemLevel} • equip {candidate.EquipLevel}\n{candidate.RankingSummary}\n{candidate.SourceLabel}");
        ImGui.SameLine();
        ImGui.TextDisabled($" {candidate.ItemLevel}");
    }

    private void DrawPotentialRecommendations(
        GearSlot slot,
        IReadOnlyList<CofferSuggestion> potential,
        RecommendationCandidate? exact,
        RecommendationCandidate? current)
    {
        var best = potential[0];
        var isUpgrade = IsPotentialUpgradeForSlot(best, exact, current);

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3f);
        DrawItemIcon(best.ItemId, 20);
        ImGui.SameLine();
        ImGui.TextColored(isUpgrade ? ReadyColor : PotentialColor, isUpgrade ? "Potential ↑" : "Potential");
        ImGui.SameLine();
        ImGui.TextUnformatted(best.ItemName);
        ImGui.SameLine();
        ImGui.TextDisabled(best.ItemLevel > 0 ? $" iLvl {best.ItemLevel}" : " iLvl ?");
        ImGui.SameLine();
        ImGui.TextDisabled($" • open to reveal • {ShortSource(best.SourceLabel)}");

        if (ImGui.IsItemHovered())
        {
            var benchmark = exact?.ItemLevel ?? current?.ItemLevel ?? 0;
            var comparison = best.ItemLevel > 0
                ? $"Coffer iLvl {best.ItemLevel} vs known {SlotName(slot)} iLvl {benchmark}."
                : "The coffer has no useful iLvl metadata.";
            ImGui.SetTooltip($"Potential recommendation, not an exact item.\n{comparison}\nGearScout will re-evaluate after you open it; it never opens coffers automatically.");
        }

        if (potential.Count > 1)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($" +{potential.Count - 1}");
            if (ImGui.IsItemHovered())
            {
                var others = string.Join("\n", potential.Skip(1).Select(x => $"• {x.ItemName} — {(x.ItemLevel > 0 ? $"iLvl {x.ItemLevel}" : "iLvl ?")} — {ShortSource(x.SourceLabel)}"));
                ImGui.SetTooltip($"Other potential sources for {SlotName(slot)}:\n{others}");
            }
        }
    }

    private IReadOnlyList<CofferSuggestion> GetPotentialCoffers(GearSlot slot) =>
        cofferSuggestions
            .Where(x => x.PotentialSlots.Contains(slot))
            .OrderByDescending(x => x.ItemLevel)
            .ThenBy(x => x.ItemName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static bool IsPotentialUpgradeForSlot(
        CofferSuggestion coffer,
        RecommendationCandidate? exact,
        RecommendationCandidate? current)
    {
        if (coffer.ItemLevel == 0)
            return false;

        var benchmark = exact?.ItemLevel ?? current?.ItemLevel ?? 0;
        return coffer.ItemLevel > benchmark;
    }

    private void DrawCompactPlan(GearTarget target, bool detached = true)
    {
        var plan = plugin.Configuration.ActivePlan;
        if (plan == null) return;
        var done = plan.Items.Count(x => x.State == PlanItemState.Equipped);

        if (detached)
        {
            ImGui.TextUnformatted($"{plan.TargetName}  •  {GetJobAbbreviation(plan.JobId)} {plan.Level}");
            ImGui.SameLine();
            ImGui.TextDisabled($"{done}/{plan.Items.Count}");
        }

        if (ImGui.SmallButton(plugin.Configuration.HighlightPlanItems ? "Highlight: ON" : "Highlight: OFF"))
        {
            plugin.Configuration.HighlightPlanItems = !plugin.Configuration.HighlightPlanItems;
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Also highlights matching visible cells in the Glamour Dresser.");
        ImGui.SameLine();
        if (ImGui.SmallButton("↻##planrefresh")) RequestRefresh();
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
        {
            plugin.PlanService.Clear();
            RequestRefresh();
            return;
        }
        ImGui.Separator();

        foreach (var item in plan.Items.OrderBy(x => x.Slot))
            DrawPlanRow(item);
    }

    private void DrawPlanRow(PlannedGearItem item)
    {
        var color = item.State switch
        {
            PlanItemState.ToRetrieve => RetrieveColor,
            PlanItemState.Retrieved => ReadyColor,
            PlanItemState.Equipped => EquippedColor,
            _ => MissingColor,
        };
        var glyph = item.State switch
        {
            PlanItemState.ToRetrieve => "↓",
            PlanItemState.Retrieved => "→",
            PlanItemState.Equipped => "✓",
            _ => "?",
        };

        var pos = ImGui.GetCursorScreenPos();
        if (plugin.Configuration.HighlightPlanItems && item.State != PlanItemState.Equipped)
        {
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(
                pos - new Vector2(3, 2),
                pos + new Vector2(ImGui.GetContentRegionAvail().X, 34),
                ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, .10f)),
                4f);
        }

        ImGui.TextColored(color, glyph);
        ImGui.SameLine();
        DrawItemIcon(item.ItemId, 28);
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextUnformatted(item.ItemName);
        ImGui.TextDisabled($"{SlotName(item.Slot)} • iLvl {item.ItemLevel} • {ShortSource(item.CurrentSourceLabel)}");
        ImGui.EndGroup();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(StateHelp(item));
    }

    private void DrawItemIcon(uint itemId, float size)
    {
        if (!plugin.DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
        {
            ImGui.Dummy(new Vector2(size));
            return;
        }

        try
        {
            var tex = plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.Icon)).GetWrapOrDefault();
            if (tex != null)
            {
                ImGui.Image(tex.Handle, new Vector2(size));
                return;
            }
        }
        catch { }

        ImGui.Dummy(new Vector2(size));
    }

    private static string StateHelp(PlannedGearItem item) => item.State switch
    {
        PlanItemState.ToRetrieve => $"Retrieve from {item.CurrentSourceLabel}",
        PlanItemState.Retrieved => "Item is in Inventory / Armoury: equip it.",
        PlanItemState.Equipped => "Equipped.",
        _ => "GearScout cannot currently locate this item.",
    };

    private static Vector4 SourceColor(ItemSourceKind kind) => kind switch
    {
        ItemSourceKind.EquippedTarget => ReadyColor,
        ItemSourceKind.PlayerInventory or ItemSourceKind.Armoury => EquippedColor,
        ItemSourceKind.EquippedElsewhere => RetrieveColor,
        _ => Vector4.One,
    };

    private static string ShortSource(string s) => s
        .Replace("Glamour Dresser", "Dresser")
        .Replace("Glamour dresser", "Dresser")
        .Replace("Player Inventory", "Inventory")
        .Replace("Armoury Chest", "Armoury");

    private string GetJobAbbreviation(uint id) =>
        plugin.DataManager.GetExcelSheet<ClassJob>().TryGetRow(id, out var j) ? j.Abbreviation.ToString() : $"Job {id}";

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
