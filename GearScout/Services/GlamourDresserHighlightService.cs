using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace GearScout.Services;

/// <summary>
/// Draws a non-invasive overlay around visible Glamour Dresser cells that correspond
/// to items still present in the active GearScout retrieval plan.
///
/// The game already exposes the 50 visible cell nodes and the agent exposes the 50
/// item indexes for the current page, so this works with sorting, filtering and search
/// without trying to recreate the dresser layout ourselves.
/// </summary>
public unsafe sealed class GlamourDresserHighlightService
{
    private readonly Configuration configuration;
    private readonly IPluginLogAdapter log;

    // These offsets are the public API-15 FFXIVClientStructs layout. Keeping them here
    // avoids depending on generated accessors for internal fixed arrays.
    private const int ItemSlotsOffset = 0x238;
    private const int PrismBoxItemsOffset = 0x08;
    private const int PageItemIndexesOffset = 0x11AD98;
    private const int VisibleSlotCount = 50;
    private const int MaxPrismBoxItems = 8000;

    public GlamourDresserHighlightService(Configuration configuration, Dalamud.Plugin.Services.IPluginLog log)
    {
        this.configuration = configuration;
        this.log = new IPluginLogAdapter(log);
    }

    public void Draw(nint addonAddress)
    {
        if (!configuration.HighlightPlanItems || addonAddress == 0)
            return;

        var plan = configuration.ActivePlan;
        if (plan is not { Items.Count: > 0 })
            return;

        var targetIds = plan.Items
            .Where(x => x.State == PlanItemState.ToRetrieve && IsDresserSource(x.CurrentSourceLabel))
            .Select(x => x.ItemId)
            .ToHashSet();
        if (targetIds.Count == 0)
            return;

        try
        {
            var framework = Framework.Instance();
            if (framework == null || framework->UIModule == null)
                return;

            var agentBase = framework->UIModule->GetAgentModule()->GetAgentByInternalId(AgentId.MiragePrismPrismBox);
            var agent = (AgentMiragePrismPrismBox*)agentBase;
            if (agent == null || !agent->IsAgentActive() || !agent->IsDataLoaded || agent->Data == null)
                return;

            var addon = (AddonMiragePrismPrismBox*)addonAddress;
            var data = agent->Data;
            var slots = (AddonMiragePrismPrismBox.ItemSlot*)((byte*)addon + ItemSlotsOffset);
            var pageIndexes = (int*)((byte*)data + PageItemIndexesOffset);
            var prismItems = (PrismBoxItem*)((byte*)data + PrismBoxItemsOffset);
            var draw = ImGui.GetForegroundDrawList();

            for (var visible = 0; visible < VisibleSlotCount; visible++)
            {
                var itemIndex = pageIndexes[visible];
                if (itemIndex < 0 || itemIndex >= MaxPrismBoxItems)
                    continue;

                var itemId = prismItems[itemIndex].ItemId;
                if (itemId == 0 || !targetIds.Contains(itemId))
                    continue;

                var node = slots[visible].SlotRes;
                if (node == null || node->IsDrawDisabled)
                    continue;

                var min = new Vector2(node->ScreenX, node->ScreenY);
                var width = MathF.Abs(node->Width * node->ScaleX);
                var height = MathF.Abs(node->Height * node->ScaleY);
                if (width < 4f || height < 4f)
                    continue;

                var max = min + new Vector2(width, height);
                var fill = ImGui.GetColorU32(new Vector4(1f, 0.72f, 0.12f, 0.20f));
                var border = ImGui.GetColorU32(new Vector4(1f, 0.82f, 0.18f, 1f));
                draw.AddRectFilled(min, max, fill, 4f);
                draw.AddRect(min, max, border, 4f, ImDrawFlags.None, 3f);
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Glamour Dresser highlight failed for this frame");
        }
    }

    private static bool IsDresserSource(string label) =>
        label.Contains("Glamour Dresser", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("Dresser", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("Commode", StringComparison.OrdinalIgnoreCase);

    // Tiny adapter keeps the hot path from depending on logging overload changes.
    private sealed class IPluginLogAdapter
    {
        private readonly Dalamud.Plugin.Services.IPluginLog inner;
        public IPluginLogAdapter(Dalamud.Plugin.Services.IPluginLog inner) => this.inner = inner;
        public void Debug(Exception ex, string message) => inner.Debug(ex, message);
    }
}
