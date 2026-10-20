using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;
using GameCore.HotUpdate.ReduxUI;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using Vuplex.WebView;
using Bags = Il2CppSystem.Collections.Generic.List<GameCore.HotUpdate.ReduxUI.Data_Bag>;
using Containers = Il2CppSystem.Collections.Generic.List<GameCore.HotUpdate.TradeContainerInfo>;
using Fridges = Il2CppSystem.Collections.Generic.List<GameCore.HotUpdate.CookingFridgeInfo>;
using Cabinets = Il2CppSystem.Collections.Generic.List<GameCore.HotUpdate.ToolCabinetInfo>;

[BepInPlugin("local.survivallog.homeaccess", "Home Container Access", "1.2.2")]
[BepInDependency("com.basebutler.mod")]
public sealed class HomeAccessPlugin : BasePlugin
{
    internal static ManualLogSource LogSource;
    internal static bool LayoutReady;
    internal static ConfigEntry<bool> Backpack, Trade, Brew, Fuel, Remote, SoftwarePaint, DisableGpu;
    internal static ConfigEntry<string> CrashDumps;
    public override void Load()
    {
        LogSource = Log;
        try { Log.LogInfo("[HomeAccess] " + AdaptiveLayout.Apply(Paths.GameRootPath, false)); LayoutReady = true; }
        catch (Exception e) { Log.LogWarning("[HomeAccess] Layout unsupported; extra backpack tabs disabled: " + e.Message); }
        using (var resource = typeof(HomeAccessPlugin).Assembly.GetManifestResourceStream("HomeButler.ContainerUI.js"))
        using (var reader = new System.IO.StreamReader(resource)) {
            var script=reader.ReadToEnd();
            foreach(var page in PanelLayout.Pages) {
                try { Log.LogInfo("[HomeAccess] " + PanelLayout.Apply(Paths.GameRootPath,page,script,false)); }
                catch(Exception e) { Log.LogWarning("[HomeAccess] Panel controls skipped for "+page+": "+e.Message); }
            }
        }
        Backpack = Config.Bind("Containers", "Backpack", true, "家中背包/货舱界面增加有效储物容器标签。");
        Remote = Config.Bind("Containers", "RemoteHomeAccess", true, "外出时也可通过背包标签访问家中储物；物品转移仍使用原版容量与锁定检查。");
        Trade = Config.Bind("Containers", "Trade", true, "家中无人机交易增加有效储物容器标签。");
        Brew = Config.Bind("Containers", "Brew", true, "酿酒可从有效家中储物容器取料。");
        Fuel = Config.Bind("Containers", "Fuel", true, "暖气/发电机显示家中储物标签与横向滚动条；PageUp/PageDown 仍可用。");
        SoftwarePaint = Config.Bind("WebView", "UseSoftwarePaint", true, "排查选项：禁用 D3D11 加速纹理绘制，可能增加 CPU 开销；重启生效，尚未证明解决崩溃。");
        DisableGpu = Config.Bind("WebView", "DisableGpu", false, "排查选项：向 Chromium 传 --disable-gpu 绕过 GPU 合成；若崩溃消失说明问题在 GPU/驱动层。");
        CrashDumps = Config.Bind("WebView", "CrashDumpsDir", "BepInEx/webview-crash-dumps", "Chromium 崩溃 dump 输出目录（相对游戏根目录）；下次崩溃后可把 dump 发给开发者。");
        try
        {
            var dir = CrashDumps.Value;
            if (!System.IO.Path.IsPathRooted(dir)) dir = System.IO.Path.Combine(Paths.GameRootPath, dir);
            System.IO.Directory.CreateDirectory(dir);
            var args = "--crash-dumps-dir=\"" + dir + "\"";
            if (DisableGpu.Value) args += " --disable-gpu";
            StandaloneWebView.SetCommandLineArguments(args);
            Log.LogInfo("[HomeAccess] Chromium args: " + args);
        }
        catch (Exception e) { Log.LogWarning("[HomeAccess] Chromium args unavailable: " + e.Message); }
        var h = new Harmony("local.survivallog.homeaccess");
        h.PatchAll(typeof(HomeAccessPlugin).Assembly);
        AddComponent<HomeAccessWorker>();
        if (SoftwarePaint.Value)
        {
            try { StandaloneWebView.SetAcceleratedPaintEnabled(false); Log.LogInfo("[HomeAccess] WebView fallback paint requested; stability unverified."); }
            catch (Exception e) { Log.LogWarning("[HomeAccess] Fallback paint unavailable: " + e.Message); }
        }
        Log.LogInfo("[HomeAccess] 1.2.2: remote storage; scrollbars; fuel grid refresh; per-save container names and sorting.");
    }
}

public sealed class HomeAccessWorker : MonoBehaviour
{
    public HomeAccessWorker(IntPtr ptr) : base(ptr) { }
    public void Update() { ContainerCatalog.Tick(); if(HomeAccessPlugin.Fuel.Value) FuelPanels.Tick(); }
}

internal static class HomeSources
{
    internal sealed class Source { internal long Owner; internal int Furniture, Bag; internal string Name; }
    // Lifetime is ONE synchronous public action. Never retain native state pointers.
    [ThreadStatic] internal static List<Source> OpeningBackpack;
    private static MethodInfo excluded, excludedName, nameOf;
    internal static T State<T>() where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
        => BaseSingleton<ReduxUISystem>._Instance?.reduxStoreLayer?.GetState<T>(Il2CppType.Of<T>());
    internal static T Data<T>() where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
        => BaseSingleton<ReduxUISystem>._Instance?.reduxStoreLayer?.GetData<T>(Il2CppType.Of<T>());
    internal static void Safe(Action action)
    {
        try { action(); }
        catch (Exception e) { HomeAccessPlugin.LogSource.LogWarning("[HomeAccess] Extension skipped: " + e); }
    }
    internal static List<Source> Collect(long except = 0, bool remote = false)
    {
        var result = new List<Source>();
        var world = BaseSingleton<BattleLogicWorld>._Instance;
        var agents = world?._AgentManager;
        var items = world?._ItemManager;
        var configs = BaseSingleton<ConfigManager>._Instance;
        var role = agents?.GetLeadingRole();
        if (role == null || items == null || configs == null) { Trace("world/player/config not ready"); return result; }
        if (!remote && !Reducer_Web_BackpackUI.IsAtHomeScene()) { Trace("not home; map=" + Data<State_Data_Player>()?.MapConfigId); return result; }
        var itemState = Data<State_Data_Item>();
        if (itemState?.BagCache == null) { Trace("BagCache not ready"); return result; }
        if (excluded == null)
        {
            Type policy = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == "BaseButler") policy = assembly.GetType("BaseButler.SourceExpand.CookingBagPatch");
            if (policy == null) return result;
            excluded = AccessTools.Method(policy, "IsStorageExcluded");
            excludedName = AccessTools.Method(policy, "IsStorageExcludedName");
            nameOf = AccessTools.Method(policy, "StorageResolveName");
        }
        var home = agents.GetHomeMapId();
        if (home <= 0) return result;
        // Remote access must not filter home furniture by the exploration floor.
        var furniture = agents.GetFurnituresWithBag(home, false, true, !remote);
        // Scene-scoped native queries can return zero outside home even with
        // floorScope disabled. Enumerate live logical agents, never spawn copies
        // or synthesize inventory entries from an old save.
        if (remote && !Reducer_Web_BackpackUI.IsAtHomeScene())
        {
            furniture = new Il2CppSystem.Collections.Generic.List<Furniture>();
            var all = agents.GetAllAgent();
            var maps = new Dictionary<int,int>();
            if (all != null) for (int i = 0; i < all.Count; i++)
            {
                var f = all[i]?.TryCast<Furniture>();
                if (f == null) continue;
                maps[f.MapConfigId] = maps.TryGetValue(f.MapConfigId,out var n) ? n+1 : 1;
                if (agents.IsHomeMap(f.MapConfigId)) furniture.Add(f);
            }
            Trace("Remote live scan: agents="+(all?.Count ?? 0)+", homeFurniture="+furniture.Count+", mapCounts="+JsonSerializer.Serialize(maps));
        }
        var seen = new HashSet<long>();
        if (furniture == null) return result;
        int rejectedState = 0, rejectedCache = 0, rejectedBag = 0, rejectedPolicy = 0;
        string sample = "";
        for (int i = 0; i < furniture.Count; i++)
        {
            var f = furniture[i];
            if (f == null || f.InstanceId == 0 || f.InstanceId == except || f.IsDead || f.IsBagLocked
                || !agents.IsHomeMap(f.MapConfigId) || !seen.Add(f.InstanceId)) { rejectedState++; continue; }
            var def = configs.Get_Config_Furniture(f.AgentConfigId);
            if (def == null || def.BagId <= 0 || !itemState.BagCache.ContainsKey(f.InstanceId)) {
                rejectedCache++; if (sample == "") sample = $"furniture={f.AgentConfigId}, defBag={def?.BagId}, cached={itemState.BagCache.ContainsKey(f.InstanceId)}"; continue;
            }
            int bag = itemState.BagCache[f.InstanceId];
            // UI reducers read BagCache, so a valid furniture BagId alone is insufficient.
            // Native bag entries can identify different owner kinds. Resolve
            // dimensions via ItemManager rather than guessing the cache ID kind.
            if (bag <= 0) { rejectedBag++; if (sample == "") sample = $"furniture={f.AgentConfigId}, defBag={def.BagId}, cacheValue={bag}"; continue; }
            if (ItemManager.IsLockedBagOwner(f.InstanceId) || !items.TryGetTargetBagConfig(f.InstanceId, out var size, out _, out _)
                || size.x <= 0 || size.y <= 0) { rejectedState++; continue; }
            if ((bool)excluded.Invoke(null, new object[] { f.AgentConfigId })) { rejectedPolicy++; continue; }
            string name = (string)nameOf.Invoke(null, new object[] { f.AgentConfigId });
            if ((bool)excludedName.Invoke(null, new object[] { name })) { rejectedPolicy++; continue; }
            result.Add(new Source { Owner = f.InstanceId, Furniture = f.AgentConfigId, Bag = bag, Name = name });
        }
        Trace($"home={home}, furniture={furniture.Count}, accepted={result.Count}, state={rejectedState}, cache={rejectedCache}, bag={rejectedBag}, policy={rejectedPolicy}; {sample}");
        return result;
    }
    private static void Trace(string message) => HomeAccessPlugin.LogSource.LogInfo("[HomeAccess] Source check: " + message);

    internal static void ExpandTrade()
    {
        if (!HomeAccessPlugin.Trade.Value) return;
        var state = State<State_Web_TradeUI>();
        if (state?.Containers == null) return;
        var sources = Collect(TradeDestination.For(state));
        if (sources.Count == 0) return;
        var list = new Containers();
        var seen = new HashSet<long> { state.BagOwnerId, TradeDestination.For(state) };
        for (int i = 0; i < state.Containers.Count; i++)
        {
            var entry = state.Containers[i];
            if (entry != null && seen.Add(entry.OwnerId)) list.Add(entry);
        }
        int added = 0;
        foreach (var s in sources)
            if (seen.Add(s.Owner)) { list.Add(new TradeContainerInfo { OwnerId = s.Owner, FurnitureConfigId = s.Furniture, IsFridge = false }); added++; }
        if (added == 0) return;
        // Use the game's public action AFTER ShowUI. No recursive reducer mutation.
        Ac_TradeUI_SetContainerTabs.SendAction(list);
        HomeAccessPlugin.LogSource.LogInfo($"[HomeAccess] Trade added={added}, nativeTabs={state.BagTabs?.Count}, jsonChars={state.BagTabsJson?.Value?.Length}");
    }

    internal static void AddFridges(string panel, long target, Fridges list)
    {
        if (list == null) return;
        var sources = Collect(target);
        var seen = new HashSet<long>();
        for (int i = 0; i < list.Count; i++) if (list[i] != null) seen.Add(list[i].OwnerId);
        int added = 0;
        foreach (var s in sources)
            if (seen.Add(s.Owner)) { list.Add(new CookingFridgeInfo { OwnerId = s.Owner, FurnitureConfigId = s.Furniture, Locked = false }); added++; }
        HomeAccessPlugin.LogSource.LogInfo($"[HomeAccess] {panel} added={added}");
    }

    internal static void AddCabinets(string panel, long target, Cabinets list)
    {
        if (list == null) return;
        var sources = Collect(target);
        var seen = new HashSet<long>();
        for (int i = 0; i < list.Count; i++) if (list[i] != null) seen.Add(list[i].OwnerId);
        int added = 0;
        foreach (var s in sources)
            if (seen.Add(s.Owner)) { list.Add(new ToolCabinetInfo { OwnerId = s.Owner, FurnitureConfigId = s.Furniture }); added++; }
        HomeAccessPlugin.LogSource.LogInfo($"[HomeAccess] {panel} added={added}");
    }
}

[HarmonyPatch(typeof(Ac_BackpackUI_OpenUI), nameof(Ac_BackpackUI_OpenUI.SendAction))]
internal static class OpenBackpack
{
    private static void Prefix(Bags __0, out List<HomeSources.Source> __state)
    {
        __state = HomeSources.OpeningBackpack;
        HomeSources.OpeningBackpack = null;
        if (HomeAccessPlugin.Backpack.Value && HomeAccessPlugin.LayoutReady)
            HomeSources.Safe(() => HomeSources.OpeningBackpack = HomeSources.Collect(__0 != null && __0.Count > 1 ? __0[1].OwnerId : 0, HomeAccessPlugin.Remote.Value));
    }
    private static Exception Finalizer(Exception __exception, List<HomeSources.Source> __state)
    { HomeSources.OpeningBackpack = __state; return __exception; }
}

[HarmonyPatch(typeof(Reducer_Web_BackpackUI), nameof(Reducer_Web_BackpackUI.BuildATabs))]
internal static class BackpackTabs
{
    private static void Postfix(State_Web_BackpackUI __0, State_Data_Item __1)
    {
        var sources = HomeSources.OpeningBackpack;
        if (sources == null || sources.Count == 0 || __0?.ATabOwnerIds == null) return;
        HomeSources.Safe(() =>
        {
            var original = __0.ATabOwnerIds;
            var originalJson = __0.ATabsJson.Value;
            var replacement = new Il2CppSystem.Collections.Generic.List<long>();
            for (int i = 0; i < original.Count; i++) replacement.Add(original[i]);
            foreach (var s in sources)
                if (s.Owner != __0.OwnerId_B && !replacement.Contains(s.Owner)) replacement.Add(s.Owner);
            if (replacement.Count == original.Count) return;
            try
            {
                __0.ATabOwnerIds = replacement;
                var json = Reducer_Web_BackpackUI.BuildATabsJson(__0, __1);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.GetArrayLength() != replacement.Count)
                    throw new InvalidOperationException("Native backpack tab count does not match; restoring original tabs");
                // Keep native dimensions/flags and only give added tabs their furniture names.
                var labels = JsonNode.Parse(json).AsArray();
                for (int i = 0; i < replacement.Count; i++) labels[i]["hbOwner"] = replacement[i].ToString();
                for (int i = original.Count; i < replacement.Count; i++)
                    foreach (var s in sources)
                        if (s.Owner == replacement[i]) { labels[i]["name"] = s.Name; break; }
                json = labels.ToJsonString();
                __0.ATabsJson.Value = json;
                HomeAccessPlugin.LogSource.LogInfo($"[HomeAccess] Backpack nativeTabs={replacement.Count}, jsonChars={json.Length}");
            }
            catch { __0.ATabOwnerIds = original; __0.ATabsJson.Value = originalJson; throw; }
        });
    }
}

[HarmonyPatch(typeof(Ac_TradeUI_ShowUI), nameof(Ac_TradeUI_ShowUI.SendAction))]
internal static class TradeTabs
{
    private static void Postfix() => HomeSources.Safe(HomeSources.ExpandTrade);
}

[HarmonyPatch(typeof(Ac_Item_GetBrewBagList), nameof(Ac_Item_GetBrewBagList.SendAction))]
internal static class BrewSources
{
    private static void Prefix(long TargetId, Fridges Fridges)
    {
        if (!HomeAccessPlugin.Brew.Value) return;
        HomeSources.Safe(() => HomeSources.AddFridges("Brew", TargetId, Fridges));
    }
}

[HarmonyPatch(typeof(Ac_Item_GetRatCageBagList), nameof(Ac_Item_GetRatCageBagList.SendAction))]
internal static class RatCageSources
{
    private static void Prefix(long TargetId, Fridges Fridges)
        => HomeSources.Safe(() => HomeSources.AddFridges("RatCage", TargetId, Fridges));
}

[HarmonyPatch(typeof(Ac_Item_GetCompressorBagList), nameof(Ac_Item_GetCompressorBagList.SendAction))]
internal static class CompressorSources
{
    private static void Prefix(long TargetId, long TrayOwnerId, Cabinets Cabinets, Fridges Fridges)
        => HomeSources.Safe(() => HomeSources.AddCabinets("Compressor", TargetId, Cabinets));
}

[HarmonyPatch(typeof(Ac_Item_GetShredderBagList), nameof(Ac_Item_GetShredderBagList.SendAction))]
internal static class ShredderSources
{
    private static void Prefix(long TargetId, long TrayOwnerId, Cabinets Cabinets)
        => HomeSources.Safe(() => HomeSources.AddCabinets("Shredder", TargetId, Cabinets));
}

// Heater / FuelGenerator panels natively show only the player backpack. Keep the
// native panel, switch its source with PageUp/PageDown. State edits only happen
// on a key press; title refresh is skipped unless it actually changed.
internal static class FuelPanels
{
    internal sealed class Session
    {
        internal long Target;
        internal string Name, LastTitle = "";
        internal IntPtr World, Agents;
        internal readonly List<HomeSources.Source> Sources = new();
        internal int Index;
    }
    private static readonly Dictionary<string, Session> Sessions = new();

    internal static void Prepare(string panel, Bags bags, long target, string name)
    {
        var world = BaseSingleton<BattleLogicWorld>._Instance;
        if (world?._AgentManager == null) return;
        var session = new Session { Target = target, Name = name, World = world.Pointer, Agents = world._AgentManager.Pointer };
        var seen = new HashSet<long>();
        if (bags != null) for (int i = 0; i < bags.Count; i++)
            if (bags[i] != null && seen.Add(bags[i].OwnerId))
                session.Sources.Add(new HomeSources.Source { Owner = bags[i].OwnerId, Name = i == 0 ? "背包" : "随身容器" });
        foreach (var s in HomeSources.Collect(target))
            if (seen.Add(s.Owner)) session.Sources.Add(s);
        Sessions[panel] = session;
        ContainerCatalog.RefreshRequested = true;
        HomeAccessPlugin.LogSource.LogInfo($"[HomeAccess] {panel}: {session.Sources.Count} sources; mouse tabs and scrollbar enabled");
    }

    internal static object Describe(string panel)
    {
        if(!Sessions.TryGetValue(panel,out var s)) return null;
        var rows=new List<object>();
        foreach(var item in s.Sources) rows.Add(new { owner=item.Owner.ToString(), name=item.Name });
        return new { owner=s.Sources.Count>s.Index?s.Sources[s.Index].Owner.ToString():"", sources=rows };
    }
    internal static void Select(string panel,long owner)
    {
        var layer=BaseSingleton<ReduxUISystem>._Instance?.GetWebUILayer();
        if(layer==null || !layer.IsPageActive(panel) || !Sessions.TryGetValue(panel,out var s)) return;
        var world=BaseSingleton<BattleLogicWorld>._Instance;
        if(world==null || world.Pointer!=s.World || world._AgentManager?.Pointer!=s.Agents) return;
        var index=s.Sources.FindIndex(x=>x.Owner==owner);
        if(index<0 || ItemManager.IsLockedBagOwner(owner)) return;
        var manager=world._ItemManager;
        if(!manager.TryGetTargetBagConfig(owner,out var size,out _,out _) || size.x<=0 || size.y<=0) return;
        if(panel=="Heater") {
            var state=HomeSources.State<State_Web_Heater>();
            if(state==null || state.CurrentFurnitureId!=s.Target) return;
            state.BagOwnerId.Value=owner; state.BagCols.Value=size.x; state.BagRows.Value=size.y;
            state.HeaterName.Value=s.Name;
            Ac_Heater_RefreshBag.SendAction();
        } else if(panel=="FuelGenerator") {
            var state=HomeSources.State<State_Web_FuelGenerator>();
            if(state==null || state.CurrentFurnitureId!=s.Target) return;
            state.BagOwnerId.Value=owner; state.BagCols.Value=size.x; state.BagRows.Value=size.y;
            state.GeneratorName.Value=s.Name;
            Ac_FuelGenerator_RefreshBag.SendAction();
        } else return;
        s.Index=index;
        HomeAccessPlugin.LogSource.LogInfo("[HomeAccess] "+panel+": native full-data source refresh, owner="+owner);
    }
    internal static void Tick()
    {
        if (Sessions.Count == 0) return;
        try
        {
            var ui = BaseSingleton<ReduxUISystem>._Instance;
            var layer = ui?.GetWebUILayer();
            if (layer == null) return;
            string panel = layer.IsPageActive("Heater") ? "Heater" : layer.IsPageActive("FuelGenerator") ? "FuelGenerator" : null;
            if (panel == null || !Sessions.TryGetValue(panel, out var s) || s.Sources.Count == 0) return;
            var world = BaseSingleton<BattleLogicWorld>._Instance;
            if (world == null || world.Pointer != s.World || world._AgentManager == null || world._AgentManager.Pointer != s.Agents) { Sessions.Remove(panel); return; }
            int dir = (Input.GetKeyDown(KeyCode.PageDown) || Input.GetKeyDown(KeyCode.D)) ? 1 : (Input.GetKeyDown(KeyCode.PageUp) || Input.GetKeyDown(KeyCode.A)) ? -1 : 0;
            var manager = world._ItemManager;
            if (manager == null) return;
            if(dir!=0 && ContainerCatalog.EditingPage==null) {
                var index=(s.Index+dir+s.Sources.Count)%s.Sources.Count;
                Select(panel,s.Sources[index].Owner);
                ContainerCatalog.Publish(layer,panel);
            }
        }        catch (Exception e) { HomeAccessPlugin.LogSource.LogWarning("[HomeAccess] Fuel panel switch skipped: " + e.Message); }
    }
}

[HarmonyPatch(typeof(Ac_Heater_Open), nameof(Ac_Heater_Open.SendAction))]
internal static class HeaterSources
{
    private static void Prefix(Bags BagList, long TargetId, string FurnitureName)
    { if (HomeAccessPlugin.Fuel.Value) HomeSources.Safe(() => FuelPanels.Prepare("Heater", BagList, TargetId, FurnitureName)); }
}

[HarmonyPatch(typeof(Ac_FuelGenerator_Open), nameof(Ac_FuelGenerator_Open.SendAction))]
internal static class GeneratorSources
{
    private static void Prefix(Bags BagList, long TargetId, string FurnitureName)
    { if (HomeAccessPlugin.Fuel.Value) HomeSources.Safe(() => FuelPanels.Prepare("FuelGenerator", BagList, TargetId, FurnitureName)); }
}

// Observe failures; preserve the game's recovery, save and exit behavior.
[HarmonyPatch(typeof(WebUILayer), nameof(WebUILayer.OnBrowserProcessFailed))]
internal static class BrowserFailure
{
    private static void Prefix(BrowserProcessFailedEventArgs __1)
        => HomeAccessPlugin.LogSource.LogError("[WebViewEvidence] Browser failed at " + DateTimeOffset.Now.ToString("O") + ": " + __1?.ErrorMessage);
}
[HarmonyPatch(typeof(WebUILayer), nameof(WebUILayer.OnWebViewTerminated))]
internal static class RendererFailure
{
    private static void Prefix(TerminatedEventArgs __1)
        => HomeAccessPlugin.LogSource.LogError("[WebViewEvidence] Renderer terminated at " + DateTimeOffset.Now.ToString("O") + ": " + __1?.Type);
}
[HarmonyPatch(typeof(WebUILayer), nameof(WebUILayer.NotifyRecoverAbandoned))]
internal static class RecoveryFailure
{
    private static void Prefix(WebUILayer __instance)
        => HomeAccessPlugin.LogSource.LogError($"[WebViewEvidence] Recovery abandoned at {DateTimeOffset.Now:O}; attempts={__instance.recoverAttempts}");
}

// Observe the engine callback entry points as well: game-level delegates did not
// emit evidence during the 20:10 failure although the renderer process vanished.
[HarmonyPatch(typeof(Vuplex.WebView.Internal.BaseWebView), nameof(Vuplex.WebView.Internal.BaseWebView.HandleTerminated))]
internal static class EngineRendererFailure
{
    private static void Prefix(string __0)
        => HomeAccessPlugin.LogSource.LogError($"[WebViewEvidence] Engine renderer termination {DateTimeOffset.Now:O}: {__0}");
}
[HarmonyPatch(typeof(StandaloneWebView), nameof(StandaloneWebView._handleBrowserProcessFailed))]
internal static class EngineBrowserFailure
{
    private static void Prefix(string __0)
        => HomeAccessPlugin.LogSource.LogError($"[WebViewEvidence] Engine browser failure {DateTimeOffset.Now:O}: {__0}");
}

// Steam no longer exposes DroneOwnerId on the UI state; obtain it from the
// public opening action, and reject values from another world or NPC.
[HarmonyPatch(typeof(Ac_TradeUI_OpenUI), nameof(Ac_TradeUI_OpenUI.SendAction))]
internal static class TradeDestination
{
    private static IntPtr world;
    private static long npc, owner;
    private static void Prefix(long NpcId, long DroneOwnerId)
    {
        world = BaseSingleton<BattleLogicWorld>._Instance?.Pointer ?? IntPtr.Zero;
        npc = NpcId; owner = DroneOwnerId;
    }
    internal static long For(State_Web_TradeUI state)
    {
        var current = BaseSingleton<BattleLogicWorld>._Instance?.Pointer ?? IntPtr.Zero;
        return current != IntPtr.Zero && current == world && state.NpcId == npc ? owner : 0;
    }
}
