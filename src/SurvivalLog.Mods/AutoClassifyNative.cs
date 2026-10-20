using System;
using System.Collections.Generic;
using System.Text.Json;
using BepInEx.Logging;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;
using GameCore.HotUpdate.ReduxUI;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using SurvivalLog.Mods;

internal static class AutoClassifyNative
{
    private sealed class Failure
    {
        public string name { get; set; }
        public int count { get; set; }
        public string reason { get; set; }
    }

    private sealed class Result
    {
        public string requestId { get; set; }
        public bool ok { get; set; }
        public int movedGroups { get; set; }
        public int movedItems { get; set; }
        public List<Failure> failures { get; set; } = new List<Failure>();
        public string error { get; set; }
    }

    private sealed class Target
    {
        public long Owner;
        public int FurnitureConfig;
        public Il2CppSystem.Collections.Generic.IReadOnlyList<int> Rule;
        public FurnitureTagComponent Tag;
    }

    internal static void Handle(WebUILayer layer, string requestJson)
    {
        ModLog.Info("Received native auto-classify request.");
        Result result;
        string requestId = null;
        try
        {
            using JsonDocument request = JsonDocument.Parse(string.IsNullOrEmpty(requestJson) ? "{}" : requestJson);
            string side = request.RootElement.TryGetProperty("side", out JsonElement sideValue) ? sideValue.GetString() : "A";
            requestId = request.RootElement.TryGetProperty("requestId", out JsonElement requestIdValue) ? requestIdValue.GetString() : null;
            result = Execute(side);
        }
        catch (Exception ex)
        {
            ModLog.Error("Native auto-classify failed: " + ex);
            result = new Result { ok = false, error = "游戏原生自动归类失败：" + ex.Message };
        }

        try
        {
            result.requestId = requestId;
            string response = JsonSerializer.Serialize(result);
            layer.BroadcastToAllPages("HB_AUTO_CLASSIFY_RESULT", response);
            ModLog.Info("Published auto-classify result: moved=" + result.movedItems + ", failures=" + result.failures.Count + ".");
        }
        catch (Exception ex) { ModLog.Error("Could not publish auto-classify result: " + ex); }
    }

    private static Result Execute(string side)
    {
        var result = new Result();
        BattleLogicWorld world = BaseSingleton<BattleLogicWorld>._Instance;
        AgentManager agents = world == null ? null : world._AgentManager;
        ItemManager items = world == null ? null : world._ItemManager;
        if (agents == null || items == null)
        {
            result.error = "游戏物品管理器尚未就绪。";
            return result;
        }

        if (side != "A" && side != "B") side = "A";
        ReduxUISystem redux = BaseSingleton<ReduxUISystem>._Instance;
        State_Web_BackpackUI ui = redux == null || redux.reduxStoreLayer == null
            ? null
            : redux.reduxStoreLayer.GetState<State_Web_BackpackUI>(Il2CppType.Of<State_Web_BackpackUI>());
        if (ui == null || ui.ATabOwnerIds == null)
        {
            result.error = "背包界面或容器列表尚未就绪。";
            return result;
        }
        long sourceOwner = side == "B" ? ui.OwnerId_B : ui.OwnerId_A;
        if (sourceOwner == 0)
        {
            result.error = "无法识别当前容器。";
            return result;
        }

        var ownerTabs = new HashSet<long>();
        foreach (long owner in ui.ATabOwnerIds) ownerTabs.Add(owner);
        if (ui.OwnerId_B != 0) ownerTabs.Add(ui.OwnerId_B);
        ownerTabs.Remove(sourceOwner);

        var taggedTargets = new List<Target>();
        var allAgents = agents.GetAllAgent();
        if (allAgents != null)
        {
            foreach (BaseAgent agent in allAgents)
            {
                Furniture furniture = agent == null ? null : ((Il2CppObjectBase)(object)agent).TryCast<Furniture>();
                if (furniture == null) continue;
                long owner = ((BaseEntity)furniture).InstanceId;
                if (owner == 0 || owner == sourceOwner || !ownerTabs.Contains(owner)) continue;
                FurnitureTagComponent tag = FindTagComponent(agent);
                if (tag == null || tag.IsRuleEmpty()) continue;
                var rule = tag.GetRule();
                if (rule == null) continue;
                taggedTargets.Add(new Target { Owner = owner, FurnitureConfig = agent.AgentConfigId, Rule = rule, Tag = tag });
            }
        }

        if (taggedTargets.Count == 0)
        {
            result.ok = true;
            result.error = "当前界面可访问的其他容器中，没有设置官方收纳规则的目标容器。";
            return result;
        }

        var before = CountByConfig(items.GetItemDataList(sourceOwner));
        if (before.Count == 0)
        {
            result.ok = true;
            return result;
        }

        // Prepare every destination first so free grid space is consolidated before
        // the official rule matcher tries to place any source items.
        foreach (Target target in taggedTargets) items.SortContainerForHub(target.Owner);

        foreach (Target target in taggedTargets)
        {
            if (items.GetItemDataList(sourceOwner) == null || items.GetItemDataList(sourceOwner).Count == 0) break;
            items.ExecuteMoveBag(sourceOwner, target.Owner, 0, false, false, target.Rule);
        }

        var after = CountByConfig(items.GetItemDataList(sourceOwner));
        foreach (KeyValuePair<int, int> entry in before)
        {
            int remaining = after.TryGetValue(entry.Key, out int count) ? count : 0;
            int moved = Math.Max(0, entry.Value - remaining);
            if (moved > 0)
            {
                result.movedGroups++;
                result.movedItems += moved;
            }
            if (remaining > 0)
            {
                Config_Item config = BaseSingleton<ConfigManager>._Instance?.Get_Config_Item(entry.Key);
                result.failures.Add(new Failure
                {
                    name = config == null ? ("物品 " + entry.Key) : (config.ItemName_Local ?? config.ItemName ?? ("物品 " + entry.Key)),
                    count = remaining,
                    reason = "没有可接收它的官方标签容器，或目标容器容量、重量及其他存入条件不允许。"
                });
            }
        }
        result.ok = result.failures.Count == 0;
        return result;
    }

    private static FurnitureTagComponent FindTagComponent(BaseAgent agent)
    {
        var components = agent.agentComponentDictionary;
        if (components == null) return null;
        foreach (BaseEntity component in components.Values)
        {
            if (component == null) continue;
            FurnitureTagComponent tag = ((Il2CppObjectBase)(object)component).TryCast<FurnitureTagComponent>();
            if (tag != null) return tag;
        }
        return null;
    }

    private static Dictionary<int, int> CountByConfig(Il2CppSystem.Collections.Generic.List<ItemData> data)
    {
        var counts = new Dictionary<int, int>();
        if (data == null) return counts;
        foreach (ItemData item in data)
        {
            if (item == null || item.ItemConfigId <= 0 || item.ItemCount <= 0) continue;
            counts[item.ItemConfigId] = counts.TryGetValue(item.ItemConfigId, out int current)
                ? current + item.ItemCount
                : item.ItemCount;
        }
        return counts;
    }
}

[HarmonyPatch(typeof(WebUIVm), "HandleCallback")]
internal static class AutoClassifyMessagePatch
{
    private static bool Prefix(WebUIVm __instance, string messageType, string jsonData)
    {
        if (!string.Equals(__instance.UIKey, "Backpack", StringComparison.Ordinal)
            || !string.Equals(messageType, "HB_AUTO_CLASSIFY", StringComparison.Ordinal)) return true;
        ReduxUISystem redux = BaseSingleton<ReduxUISystem>._Instance;
        WebUILayer layer = redux == null ? null : redux.GetWebUILayer();
        if (layer == null) return false;
        AutoClassifyNative.Handle(layer, jsonData);
        return false;
    }
}
