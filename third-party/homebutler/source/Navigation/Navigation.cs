using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using GameCore.HotUpdate;
using GameCore.HotUpdate.ReduxUI;
using HarmonyLib;

[BepInPlugin("local.survivallog.containernavigation", "Container Display Order Navigation", "1.0.0")]
[BepInDependency("local.survivallog.homeaccess")]
public sealed class NavigationPlugin : BasePlugin
{
    internal static ManualLogSource Logger;
    internal static readonly Dictionary<string, string> Actions = new() {
        { "Ac_BackpackUI_SwitchTabByKey", "BackpackUI" },
        { "Ac_Cooking_SwitchTabByKey", "Cooking" },
        { "Ac_ToolTable_SwitchTabByKey", "ToolTable" },
        { "Ac_Brew_SwitchTabByKey", "BrewPanel" },
        { "Ac_RatCage_SwitchTabByKey", "RatCage" },
        { "Ac_TradeUI_SwitchTabByKey", "TradeUI" }
    };
    internal static readonly HashSet<string> Ready = new();
    internal const string Marker = "homebutler-navigation-v100";
    public override void Load()
    {
        Logger = Log;
        using var stream = typeof(NavigationPlugin).Assembly.GetManifestResourceStream("Navigation.js");
        using var reader = new StreamReader(stream);
        var script = reader.ReadToEnd();
        var harmony = new Harmony("local.survivallog.containernavigation");
        harmony.Patch(AccessTools.Method(typeof(WebUIVm), nameof(WebUIVm.HandleCallback)),
            prefix: new HarmonyMethod(typeof(NavigationPlugin), nameof(Callback)));
        foreach (var pair in Actions) {
            try {
                var type = typeof(WebUIVm).Assembly.GetType("GameCore.HotUpdate.ReduxUI." + pair.Key, true);
                var method = AccessTools.Method(type, "SendAction", new[] { typeof(int) });
                if (method == null) throw new MissingMethodException(pair.Key, "SendAction(int)");
                InstallPage(pair.Value, script);
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(NavigationPlugin), nameof(Switch)));
                Log.LogInfo("[ContainerNavigation] ready: " + pair.Value);
            } catch (Exception e) { Log.LogWarning("[ContainerNavigation] skipped " + pair.Value + ": " + e.Message); }
        }
    }
    static void InstallPage(string page, string script)
    {
        var target = Path.Combine(Paths.GameRootPath, "SurvivalLog_Data", "StreamingAssets", "WebUI", "UI", page, page + ".html");
        var bytes = File.ReadAllBytes(target);
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.Contains("id=\"" + Marker + "\"")) return;
        if (!text.Contains("homebutler-container-ui-v120") || text.IndexOf("</body>", StringComparison.Ordinal) < 0)
            throw new InvalidDataException("Existing container sorting UI not found");
        var result = text.Replace("</body>", "<script id=\"" + Marker + "\">\n" + script + "\n</script>\n</body>");
        var backup = Path.Combine(Paths.GameRootPath, "BepInEx", "ContainerNavigation-backups");
        Directory.CreateDirectory(backup);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var original = Path.Combine(backup, page + "-" + hash + ".html");
        if (!File.Exists(original)) File.WriteAllBytes(original, bytes);
        if (!System.Linq.Enumerable.SequenceEqual(bytes, File.ReadAllBytes(target))) throw new IOException("Page changed during patching");
        var tmp = target + ".navigation.tmp";
        File.WriteAllText(tmp, result, new UTF8Encoding(false));
        File.Move(tmp, target, true);
    }
    static bool Callback(WebUIVm __instance, string messageType, string jsonData)
    {
        if (messageType != "HB_NAV_READY") return true;
        try {
            using var doc = JsonDocument.Parse(jsonData);
            var page = __instance.UIKey == "Backpack" ? "BackpackUI" : __instance.UIKey;
            if (Actions.ContainsValue(page)) {
                if (doc.RootElement.GetProperty("ready").GetBoolean()) Ready.Add(page);
                else Ready.Remove(page);
            }
        } catch (Exception e) { Logger.LogWarning("[ContainerNavigation] handshake: " + e.Message); }
        return false;
    }
    static bool Switch(MethodBase __originalMethod, int __0)
    {
        var page = Actions[__originalMethod.DeclaringType.Name];
        if (!Ready.Contains(page)) return true;
        try {
            var layer = BaseSingleton<ReduxUISystem>._Instance?.GetWebUILayer();
            if (layer == null || !layer.IsPageActive(page == "BackpackUI" ? "Backpack" : page)) return true;
            layer.BroadcastToAllPages("HB_NAV_STEP", JsonSerializer.Serialize(new { page, direction = __0 }));
            return false;
        } catch (Exception e) { Logger.LogWarning("[ContainerNavigation] native fallback: " + e.Message); return true; }
    }
}
