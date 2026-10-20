using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;
using GameCore.HotUpdate.ReduxUI;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using NativeIds = Il2CppSystem.Collections.Generic.List<long>;

[BepInPlugin("local.basebutler.drone.compatibility", "BaseButler Conservative Compatibility", "2.0.5")]
[BepInDependency("com.basebutler.mod")]
public sealed class NativeCompatibility : BasePlugin
{
    internal static ManualLogSource Logger;
    public override void Load()
    {
        Logger = Log;
        NativeFix.Initialize();
        var harmony = new Harmony("local.basebutler.native.compatibility");
        harmony.PatchAll(typeof(NativeCompatibility).Assembly);
        var merge = NativeFix.ModType("BaseButler.Stack.AutoMergeSystem");
        harmony.Patch(AccessTools.Method(merge, "IsCookingBag"),
            prefix: new HarmonyMethod(typeof(NativeFix), nameof(NativeFix.SkipWorkbenchMerge)));
        AddComponent<CompatibilityWorker>();
        Log.LogInfo("[NativeCompat] Conservative profile: quantity-aware crafting and single-use guard; extra drone/panel sources disabled; original HTML.");
    }
}

public sealed class CompatibilityWorker : MonoBehaviour
{
    public CompatibilityWorker(IntPtr ptr) : base(ptr) { }
    public void Update() => NativeFix.Tick();
}

internal static class NativeFix
{
    internal sealed class Request
    {
        internal string Json, Key;
        internal IntPtr World, Agents, State;
        internal long Bench;
    }
    private static IntPtr seenWorld, seenAgents;
    internal static Request Pending;
    internal static bool Replaying, PreparedSuccess;
    internal static string PreparedKey;
    internal static NativeIds PreparedIds;
    private static bool autoCraftPending;
    private static IntPtr autoWorld, autoAgents, autoState;
    private static string autoRecipe;
    private static MethodInfo splitMove, refresh;

    internal static Type ModType(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (assembly.GetName().Name == "BaseButler") return assembly.GetType(name, true);
        throw new InvalidOperationException("BaseButler assembly was not loaded");
    }

    private static BattleLogicWorld LiveWorld() => BaseSingleton<BattleLogicWorld>._Instance;
    private static ReduxUISystem LiveUI() => BaseSingleton<ReduxUISystem>._Instance;

    internal static void Initialize()
    {
        splitMove = AccessTools.Method(ModType("BaseButler.Stack.StackSplit"), "TrySplitMove");
        refresh = AccessTools.Method(ModType("BaseButler.Stack.SplitHalfPatch"), "RefreshToolTableState");
        if (splitMove == null || refresh == null) throw new MissingMethodException("BaseButler native helpers unavailable");
    }

    internal static bool SameWorld(IntPtr world, IntPtr agents)
    {
        var current = LiveWorld();
        return current != null && current.Pointer == world && current._AgentManager != null
            && current._AgentManager.Pointer == agents;
    }

    internal static State_Web_ToolTable State()
    {
        var ui = LiveUI();
        return ui?.reduxStoreLayer?.GetState<State_Web_ToolTable>(Il2CppType.Of<State_Web_ToolTable>());
    }

    public static bool SkipWorkbenchMerge(ItemManager im, long owner, ref bool __result)
    {
        try
        {
            if (IsWorkbench(owner)) { __result = true; return false; }
            // Source expansion makes ordinary cupboards acceptable cooking
            // sources. That must not make those cupboards stop auto-merging.
            var agent = LiveWorld()?._AgentManager?.GetAgent(owner);
            var furniture = agent?.TryCast<Furniture>();
            var config = furniture == null ? null : ConfigManager.Instance.Get_Config_Furniture(furniture.AgentConfigId);
            __result = config != null && config.CookFurnitureID > 0;
            return false;
        }
        catch { return true; }
    }

    internal static bool IsWorkbench(long owner)
    {
        try {
            var map = LiveWorld()?._ToolTableManager?.ToolTableMap;
            return owner != 0 && map != null && map.ContainsKey(owner);
        } catch { return false; }
    }

    internal static bool Queue(string json)
    {
        if (Replaying) return false;
        using var doc = JsonDocument.Parse(json);
        var key = doc.RootElement.GetProperty("recipeKey").GetString();
        var world = LiveWorld();
        var state = State();
        if (string.IsNullOrEmpty(key) || world?._AgentManager == null || state == null) return false;
        Pending = new Request { Json = json, Key = key, World = world.Pointer, Agents = world._AgentManager.Pointer,
            State = state.Pointer, Bench = state.WorkbenchOwnerId.Value };
        autoCraftPending = false;
        return true;
    }

    private static bool WorkbenchOpen() => LiveUI()?.GetWebUILayer()?.IsPageActive("ToolTable") == true;

    private static bool ExactRecipe(State_Web_ToolTable state, string key)
    {
        var map = ConfigManager.Instance.GetAllProductionListMap();
        if (map == null || !map.ContainsKey(key)) return false;
        var recipe = map[key];
        var expected = new Dictionary<int, int>();
        for (int i = 0; i < recipe.MaterialList.Count; i++)
        { int cfg = recipe.MaterialList[i]; expected.TryGetValue(cfg, out var n); expected[cfg] = n + 1; }
        var actual = Counts(LiveWorld()._ItemManager, state.WorkbenchOwnerId.Value);
        if (actual.Count != expected.Count) return false;
        foreach (var need in expected)
            if (!actual.TryGetValue(need.Key, out var n) || n != need.Value) return false;
        return true;
    }

    internal static void Tick()
    {
        try
        {
            var world = LiveWorld();
            var w = world?.Pointer ?? IntPtr.Zero;
            var a = world?._AgentManager?.Pointer ?? IntPtr.Zero;
            if (w != seenWorld || a != seenAgents)
            {
                seenWorld = w; seenAgents = a;

                autoCraftPending = false;
                if (Pending != null && !SameWorld(Pending.World, Pending.Agents)) Pending = null;
            }
            if (autoCraftPending)
            {
                autoCraftPending = false;
                var current = State();
                if (SameWorld(autoWorld, autoAgents) && WorkbenchOpen() && current != null && current.Pointer == autoState
                    && !current.IsCrafting.Value && !current.CraftBtnLocked.Value && ExactRecipe(current, autoRecipe))
                {
                    Ac_ToolTable_Make.SendAction("{\"qty\":1}");
                    NativeCompatibility.Logger.LogInfo("[NativeCompat] Craft sent through native action.");
                }
            }
            var request = Pending;
            Pending = null;
            if (request == null || !SameWorld(request.World, request.Agents)) return;
            var state = State();
            if (!WorkbenchOpen() || state == null || state.Pointer != request.State
                || state.WorkbenchOwnerId.Value != request.Bench || state.CurrentFurnitureId == 0 || state.IsCrafting.Value) return;
            PreparedIds = new NativeIds();
            PreparedKey = request.Key;
            PreparedSuccess = Fill(state, request.Key, PreparedIds);
            Replaying = true;
            try { Ac_ToolTable_RecipeClick.SendAction(request.Json); }
            finally { Replaying = false; PreparedKey = null; }
            if (PreparedSuccess && !state.CraftBtnLocked.Value)
            {
                autoCraftPending = true;
                autoWorld = request.World; autoAgents = request.Agents; autoState = state.Pointer;
                autoRecipe = request.Key;
            }
        }
        catch (Exception e)
        {
            Pending = null; autoCraftPending = false; Replaying = false; PreparedKey = null;
            NativeCompatibility.Logger.LogWarning("[NativeCompat] Operation cancelled: " + e);
        }
    }

    private static Dictionary<int, int> Counts(ItemManager im, long owner)
    {
        var counts = new Dictionary<int, int>();
        var list = im.GetItemDataList(owner);
        if (list == null) return counts;
        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i];
            if (item == null || item.ItemCount <= 0) continue;
            counts.TryGetValue(item.ItemConfigId, out var before);
            counts[item.ItemConfigId] = before + item.ItemCount;
        }
        return counts;
    }

    private static bool Move(ItemManager im, long from, long id, int cfg, int quantity, long to)
    {
        if (quantity <= 0) return true;
        // The author's splitter preserves item metadata and notifies native Redux.
        // Workbench transfers are one piece at a time, matching vanilla recipes.
        if (IsWorkbench(to))
        {
            for (int n = 0; n < quantity; n++)
                if (!MoveChecked(im, from, id, cfg, 1, to)) return false;
            return true;
        }
        return MoveChecked(im, from, id, cfg, quantity, to);
    }

    private static int CountOf(ItemManager im, long owner, int cfg)
    {
        var counts = Counts(im, owner);
        return counts.TryGetValue(cfg, out var n) ? n : 0;
    }

    private static bool MoveChecked(ItemManager im, long from, long id, int cfg, int quantity, long to)
    {
        int sourceBefore = CountOf(im, from, cfg);
        int targetBefore = from == to ? sourceBefore : CountOf(im, to, cfg);
        bool ok = (bool)splitMove.Invoke(null, new object[] { from, id, cfg, quantity, to });
        int sourceAfter = CountOf(im, from, cfg);
        int targetAfter = from == to ? sourceAfter : CountOf(im, to, cfg);
        if (from == to ? sourceBefore != sourceAfter : sourceBefore + targetBefore != sourceAfter + targetAfter)
            throw new InvalidOperationException("Material quantity conservation check failed; further transfers stopped");
        if (ok && from != to && (sourceAfter != sourceBefore - quantity || targetAfter != targetBefore + quantity))
            throw new InvalidOperationException("Transferred quantity differs from requested quantity; further transfers stopped");
        return ok;
    }

    private static bool Fill(State_Web_ToolTable state, string key, NativeIds filled)
    {
        var map = ConfigManager.Instance.GetAllProductionListMap();
        if (map == null || !map.ContainsKey(key)) throw new InvalidOperationException("Unknown native recipe: " + key);
        var recipe = map[key];
        var needed = new Dictionary<int, int>();
        for (int i = 0; i < recipe.MaterialList.Count; i++)
        {
            int cfg = recipe.MaterialList[i];
            needed.TryGetValue(cfg, out var amount); needed[cfg] = amount + 1;
        }
        var im = LiveWorld()._ItemManager;
        long bench = state.WorkbenchOwnerId.Value;
        long backpack = state.PlayerBagOwnerId;
        if (bench == 0 || backpack == 0) throw new InvalidOperationException("Workbench owners not ready");
        var owners = new List<long>();
        void Add(long owner) { if (owner != 0 && owner != bench && !owners.Contains(owner)) owners.Add(owner); }
        Add(backpack); Add(state.DrawerOwnerId.Value);
        if (state.CabinetTabs != null)
            for (int i = 0; i < state.CabinetTabs.Count; i++) Add(state.CabinetTabs[i].OwnerId.Value);

        // Move surplus back using the same checked splitter; never delete it.
        var keep = new Dictionary<int, int>(needed);
        var benchList = im.GetItemDataList(bench);
        var oldIds = new List<long>();
        if (benchList != null) for (int i = 0; i < benchList.Count; i++) oldIds.Add(benchList[i].InstanceId);
        foreach (var id in oldIds)
        {
            var item = im.GetItemData(id);
            if (item == null || item.OwnerId != bench) continue;
            keep.TryGetValue(item.ItemConfigId, out var allowance);
            int retained = Math.Min(Math.Max(allowance, 0), item.ItemCount);
            int surplus = item.ItemCount - retained;
            if (surplus > 0 && !Move(im, bench, id, item.ItemConfigId, surplus, backpack))
                throw new InvalidOperationException("No room to return extra workbench material; empty backpack cells and retry");
            keep[item.ItemConfigId] = allowance - retained;
            // Split retained multi-count rows into singleton native workbench slots.
            for (int n = 1; n < retained; n++)
                if (!Move(im, bench, id, item.ItemConfigId, 1, bench))
                    throw new InvalidOperationException("No workbench room for separate recipe pieces");
        }
        foreach (var need in needed)
        {
            var present = Counts(im, bench);
            present.TryGetValue(need.Key, out var count);
            int remaining = need.Value - count;
            foreach (long owner in owners)
            {
                if (remaining <= 0) break;
                var items = im.GetItemDataList(owner);
                var ids = new List<long>();
                if (items != null) for (int i = 0; i < items.Count; i++)
                    if (items[i] != null && items[i].ItemConfigId == need.Key) ids.Add(items[i].InstanceId);
                foreach (long id in ids)
                {
                    if (remaining <= 0) break;
                    var item = im.GetItemData(id);
                    if (item == null || item.ItemCount <= 0) continue;
                    int take = Math.Min(remaining, item.ItemCount);
                    if (!Move(im, owner, id, need.Key, take, bench))
                        throw new InvalidOperationException("Workbench transfer failed; materials remain in their containers");
                    remaining -= take;
                }
            }
        }
        var final = Counts(im, bench);
        bool exact = final.Count == needed.Count;
        foreach (var need in needed)
            exact &= final.TryGetValue(need.Key, out var count) && count == need.Value;
        var finalItems = im.GetItemDataList(bench);
        if (finalItems != null) for (int i = 0; i < finalItems.Count; i++)
            if (finalItems[i] != null && finalItems[i].ItemCount > 0) filled.Add(finalItems[i].InstanceId);
        refresh.Invoke(null, null);
        NativeCompatibility.Logger.LogInfo($"[NativeCompat] Recipe {key}: exact={exact}, workbench pieces={filled.Count}, sources={owners.Count}");
        return exact;
    }

}

[HarmonyPatch(typeof(ItemManager), nameof(ItemManager.FindStackableItem))]
internal static class WorkbenchNeverMerge
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(long ownerId, ref ItemData __result)
    {
        if (NativeFix.IsWorkbench(ownerId)) __result = null;
    }
}

[HarmonyPatch(typeof(Ac_ToolTable_RecipeClick), nameof(Ac_ToolTable_RecipeClick.SendAction))]
internal static class DeferredRecipe
{
    [HarmonyPrefix]
    private static bool Prefix(string __0)
    {
        if (NativeFix.Replaying) return true;
        try { return !NativeFix.Queue(__0); }
        catch (Exception e) { NativeCompatibility.Logger.LogWarning("[NativeCompat] Recipe dispatch unchanged: " + e.Message); return true; }
    }
}

[HarmonyPatch]
internal static class PreparedRecipeThree
{
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(Reducer_Web_ToolTable), "ApplyFillRecipe",
        new[] { typeof(State_Web_ToolTable), typeof(State_Data_Item), typeof(string) });
    private static bool Prefix(string __2, ref bool __result)
    {
        if (!NativeFix.Replaying || NativeFix.PreparedKey != __2) return true;
        __result = NativeFix.PreparedSuccess; return false;
    }
}

[HarmonyPatch]
internal static class PreparedRecipeFour
{
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(Reducer_Web_ToolTable), "ApplyFillRecipe",
        new[] { typeof(State_Web_ToolTable), typeof(State_Data_Item), typeof(string), typeof(NativeIds).MakeByRefType(), typeof(bool) });
    private static bool Prefix(string __2, ref NativeIds __3, ref bool __result)
    {
        if (!NativeFix.Replaying || NativeFix.PreparedKey != __2) return true;
        __3 = NativeFix.PreparedIds; __result = NativeFix.PreparedSuccess; return false;
    }
}

[HarmonyPatch(typeof(ActionManager), nameof(ActionManager.AddAgentAction))]
internal static class SingleUsePerClick
{
    private static int frame = -1;
    private static readonly HashSet<(long Agent, int Action, long Item)> Seen = new();
    private static bool Prefix(long __1, int __2, long __3)
    {
        try
        {
            // Only the player's item-use actions, never furniture actions,
            // crafting, NPC plans, gifts or actions with a different target.
            var world = BaseSingleton<BattleLogicWorld>._Instance;
            var agents = world?._AgentManager;
            if (agents == null || agents.LeadingRoleId != __1) return true;
            var item = world._ItemManager?.GetItemData(__3);
            if (item == null || item.ItemCount <= 1) return true;
            var config = BaseSingleton<ConfigManager>._Instance?.Get_Config_Item(item.ItemConfigId);
            if (config == null || config.UseAction <= 0) return true;
            int itemUseStart = ActionManager.ItemUseActionIdStart;
            if (config.UseAction != __2 && (itemUseStart <= 0 || __2 < itemUseStart)) return true;
            int now = Time.frameCount;
            if (now != frame) { frame = now; Seen.Clear(); }
            if (Seen.Add((__1, __2, __3))) return true;
            NativeCompatibility.Logger.LogInfo($"[NativeCompat] Repeated stacked-item use skipped: cfg={item.ItemConfigId}, action={__2}");
            return false;
        }
        catch (Exception e)
        {
            NativeCompatibility.Logger.LogWarning("[NativeCompat] Single-use guard skipped: " + e.Message);
            return true;
        }
    }
}

