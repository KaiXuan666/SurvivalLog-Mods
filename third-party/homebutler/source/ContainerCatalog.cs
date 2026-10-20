using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using BepInEx;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;
using GameCore.HotUpdate.Battle.Show;
using GameCore.HotUpdate.ReduxUI;
using HarmonyLib;

internal static class ContainerCatalog
{
    internal sealed class Label { public string Name { get; set; } = ""; public int? Rank { get; set; } public int Furniture { get; set; } }
    internal sealed class Catalog { public string Sort { get; set; } = "native"; public Dictionary<string,Label> Labels { get; set; } = new(); }
    static Catalog data = new();
    static readonly Dictionary<long,long> loadedIds = new();
    static string path;
    internal static string Session = "";
    internal static string EditingPage;
    static string visiblePage;
    internal static bool RefreshRequested;
    internal static string UIKey(string page) => page == "BackpackUI" ? "Backpack" : page;
    internal static string PageName(string key) => key == "Backpack" ? "BackpackUI" : key;
    internal static readonly HashSet<string> Pages = new() { "BackpackUI", "Cooking", "ToolTable", "BrewPanel", "Heater", "FuelGenerator", "RatCage", "Compressor", "Shredder", "TradeUI" };
    internal static void Begin(GameSaveData save)
    {
        EditingPage = null; visiblePage = null; loadedIds.Clear(); data = new Catalog(); path = null; Session = Guid.NewGuid().ToString("N");
        if (save == null || string.IsNullOrEmpty(save.FileName)) return;
        var identity = save.FileName + "|" + save.PrepRecordId + "|" + save.InitPlayerId;
        path = AdaptiveLayout.Safe(Paths.GameRootPath, "BepInEx/config/HomeButler-containers/" + AdaptiveLayout.Hash(Encoding.UTF8.GetBytes(identity)) + ".json");
        if (File.Exists(path)) {
            try { data = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(path)) ?? new Catalog(); }
            catch (Exception e) { path = null; HomeAccessPlugin.LogSource.LogWarning("Container names file unreadable; editing disabled: " + e.Message); }
        }
        if (data.Labels == null) data.Labels = new();
    }
    static void Save() { if (path == null) throw new InvalidOperationException("Save identity unavailable"); AdaptiveLayout.AtomicWrite(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data))); }
    internal static void Loaded(BaseAgent agent, AgentSave save) { if (agent != null && save != null) loadedIds[agent.InstanceId] = save.SaveInstanceId; }
    internal static Label Find(long owner, int config)
    {
        var stable = loadedIds.TryGetValue(owner, out var id) ? id : owner;
        return data.Labels.TryGetValue(stable.ToString(), out var label) && label.Furniture == config ? label : null;
    }
    internal static void Saved(BaseAgent agent, AgentSave save)
    {
        var label = Find(agent.InstanceId, agent.AgentConfigId);
        if (label != null && save.SaveInstanceId != 0) data.Labels[save.SaveInstanceId.ToString()] = label;
    }
    internal static void Flush() { if (path != null) Save(); }
    internal static void Publish(WebUILayer layer, string page)
    {
        var rows = new List<object>();
        foreach (var s in HomeSources.Collect(0, true)) {
            var label = Find(s.Owner,s.Furniture);
            rows.Add(new { owner=s.Owner.ToString(), original=s.Name, name=label?.Name ?? "", rank=label?.Rank });
        }
        var tabs = page == "Heater" || page == "FuelGenerator" ? FuelPanels.Describe(page) : null;
        layer.BroadcastToAllPages("HOME_CATALOG", JsonSerializer.Serialize(new { page, session=Session, sort=data.Sort, editable=path!=null, containers=rows, fuel=tabs }));
    }
    internal static void Handle(WebUILayer layer, string page, string type, string json)
    {
        page = PageName(page);
        if (!Pages.Contains(page) || !layer.IsPageActive(UIKey(page))) return;
        if (type == "HOME_REQUEST") { Publish(layer,page); return; }
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("session",out var session) || session.GetString()!=Session) return;
        if (type == "HOME_EDIT") {
            bool open=root.GetProperty("open").GetBoolean();
            EditingPage = open ? page : null; layer.SetKeyboardEnabled(open); return;
        }
        if (type == "HOME_FUEL_SELECT") {
            if (long.TryParse(root.GetProperty("owner").GetString(), out var owner)) FuelPanels.Select(page,owner);
            Publish(layer,page); return;
        }
        if (type != "HOME_SAVE" || path == null) return;
        var sort=root.GetProperty("sort").GetString();
        if (sort!="native" && sort!="name" && sort!="rank") throw new InvalidOperationException("Unknown sort mode");
        var sources=HomeSources.Collect(0,true);
        var allowed=new Dictionary<long,HomeSources.Source>();
        foreach(var s in sources) allowed[s.Owner]=s;
        var entries=root.GetProperty("entries");
        if(entries.GetArrayLength()>1000) throw new InvalidOperationException("Too many container labels");
        var next=new Catalog { Sort=sort, Labels=new Dictionary<string,Label>(data.Labels) };
        foreach(var entry in entries.EnumerateArray()) {
            if(!long.TryParse(entry.GetProperty("owner").GetString(),out var owner) || !allowed.TryGetValue(owner,out var source)) continue;
            var name=(entry.GetProperty("name").GetString()??"").Trim();
            if(name.Length>40 || name.IndexOfAny(new[]{'\r','\n','\0'})>=0) throw new InvalidOperationException("Invalid container name");
            var rankValue=entry.GetProperty("rank"); int? rank=rankValue.ValueKind==JsonValueKind.Null ? null : rankValue.GetInt32();
            if(rank < -9999 || rank > 9999) throw new InvalidOperationException("Sort number out of range");
            var stable=loadedIds.TryGetValue(owner,out var old)?old:owner;
            next.Labels[stable.ToString()]=new Label{Name=name,Rank=rank,Furniture=source.Furniture};
        }
        var previous=data; data=next;
        try { Save(); } catch { data=previous; throw; }
        Publish(layer,page);
    }
    internal static void Tick()
    {
        var layer=BaseSingleton<ReduxUISystem>._Instance?.GetWebUILayer();
        if(EditingPage!=null && (layer==null || !layer.IsPageActive(UIKey(EditingPage)))) { EditingPage=null; layer?.SetKeyboardEnabled(false); }
        if(layer==null)return;
        string active=null;
        foreach(var page in Pages) if(layer.IsPageActive(UIKey(page))){active=page;break;}
        if(active!=visiblePage || RefreshRequested) {
            visiblePage=active; RefreshRequested=false;
            if(active!=null) HomeSources.Safe(()=>Publish(layer,active));
        }
    }
}
[HarmonyPatch(typeof(BattleLogicWorld),nameof(BattleLogicWorld.Initialize))]
internal static class CatalogBegin { static void Prefix(GameSaveData gameSave) => HomeSources.Safe(()=>ContainerCatalog.Begin(gameSave)); }
[HarmonyPatch(typeof(BaseAgent),nameof(BaseAgent.LoadGame))]
internal static class CatalogLoad { static void Postfix(BaseAgent __instance, AgentSave agentSave) => HomeSources.Safe(()=>ContainerCatalog.Loaded(__instance,agentSave)); }
[HarmonyPatch(typeof(Furniture),nameof(Furniture.SaveGame))]
internal static class CatalogSave { static void Postfix(Furniture __instance, AgentSave agentSave) => HomeSources.Safe(()=>ContainerCatalog.Saved(__instance,agentSave)); }
[HarmonyPatch(typeof(BattleLogicWorld),nameof(BattleLogicWorld.SaveGame))]
internal static class CatalogFlush { static void Postfix() => HomeSources.Safe(ContainerCatalog.Flush); }
[HarmonyPatch(typeof(WebUIVm),nameof(WebUIVm.HandleCallback))]
internal static class CatalogMessages
{
    static bool Prefix(WebUIVm __instance,string messageType,string jsonData)
    {
        if(!messageType.StartsWith("HOME_",StringComparison.Ordinal)) return true;
        var layer=BaseSingleton<ReduxUISystem>._Instance?.GetWebUILayer();
        var source=__instance.UIKey;
        if(layer==null)return false;
        HomeAccessPlugin.LogSource.LogInfo("[HomeAccess] UI callback "+source+": "+messageType);
        try { ContainerCatalog.Handle(layer,source,messageType,jsonData); }
        catch(Exception e) { HomeAccessPlugin.LogSource.LogWarning("[HomeAccess] Container action rejected: "+e.Message); layer.BroadcastToAllPages("HOME_ERROR",JsonSerializer.Serialize(new{page=source,message=e.Message})); }
        return false;
    }
}
[HarmonyPatch(typeof(HotKeyManager),nameof(HotKeyManager.Update))]
internal static class CatalogKeyboard { static bool Prefix() { ContainerCatalog.Tick(); return ContainerCatalog.EditingPage==null; } }
