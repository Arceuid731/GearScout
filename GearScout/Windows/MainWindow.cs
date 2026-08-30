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

        // FFXIV's own Recommended Gear popup commonly occupies the right side of the
        // Character sheet. Prefer the left side so GearScout never covers the native UI.
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
        DrawCofferSuggestions();

        if (!ImGui.BeginTable("##recommendations", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, -1)))
            return;

        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Best owned", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthFixed, 125);
        ImGui.TableHeadersRow();

        foreach (var row in recommendations)
        {
            var c = row.Recommended;
            if (c == null) continue;
            var current = row.CurrentlyEquipped;
            if (plugin.Configuration.ShowOnlyUpgrades && current != null && !GearRecommendationService.IsBetter(c, current) && row.BetterReservedCandidate == null)
                continue;

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            var selected = plugin.PlanService.IsSelected(target, c);
            var old = selected;
            ImGui.Checkbox($"##plan-{row.Slot}", ref selected);
            if (selected != old)
            {
                plugin.PlanService.ToggleSelection(target, c);
                RequestRefresh();
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.TextDisabled(SlotName(row.Slot));

            ImGui.TableSetColumnIndex(2);
            DrawItemIcon(c.Entry.ItemId, 24);
            ImGui.SameLine();
            ImGui.TextUnformatted(c.ItemName + (c.Entry.IsHighQuality ? " HQ" : ""));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"iLvl {c.ItemLevel} • equip {c.EquipLevel}\n{c.RankingSummary}\n{c.SourceLabel}");
            ImGui.SameLine();
            ImGui.TextDisabled($" {c.ItemLevel}");

            ImGui.TableSetColumnIndex(3);
            ImGui.TextColored(SourceColor(c.SourceKind), ShortSource(c.SourceLabel));
        }

        ImGui.EndTable();
    }

    private void DrawCofferSuggestions()
    {
        if (cofferSuggestions.Count == 0)
            return;

        var upgrades = cofferSuggestions.Count(x => x.LooksLikeUpgrade);
        var label = upgrades > 0
            ? $"Coffers / attire  •  {upgrades} potential upgrade{(upgrades == 1 ? "" : "s")}"
            : $"Coffers / attire  •  {cofferSuggestions.Count} owned";

        if (!ImGui.CollapsingHeader($"{label}##coffers"))
            return;

        ImGui.TextDisabled("Potential only: GearScout uses the coffer's native iLvl/slot hints and never opens it automatically.");
        foreach (var coffer in cofferSuggestions)
        {
            var glyph = coffer.LooksLikeUpgrade ? "↑" : "◇";
            ImGui.TextColored(coffer.LooksLikeUpgrade ? ReadyColor : PotentialColor, glyph);
            ImGui.SameLine();
            DrawItemIcon(coffer.ItemId, 25);
            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.TextUnformatted(coffer.ItemName);
            var slots = string.Join(" / ", coffer.PotentialSlots.Select(SlotName).Distinct());
            var ilvl = coffer.ItemLevel > 0 ? $"iLvl {coffer.ItemLevel}" : "iLvl unknown";
            ImGui.TextDisabled($"Open to reveal • {slots} • {ilvl} • {ShortSource(coffer.SourceLabel)}");
            ImGui.EndGroup();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("GearScout is not claiming an exact reward here. This is a potential equipment source inferred from native game data.");
        }
        ImGui.Separator();
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
