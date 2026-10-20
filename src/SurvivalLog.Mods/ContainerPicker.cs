using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using SurvivalLog.Mods;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Show;
using GameCore.HotUpdate.Battle.Logic;
using UnityEngine;

internal static class ContainerPickerInstaller
{
    internal const string Marker = "homebutler-container-picker-v1";

    // 返回是否成功注入。HomeButler 未安装时返回 false，由统一入口记录明确提示，
    // 不影响人物增强等其他功能加载。
    internal static bool TryInstall(ManualLogSource log, string gameRootPath)
    {
        var target = Path.Combine(gameRootPath, "SurvivalLog_Data", "StreamingAssets", "WebUI", "UI", "BackpackUI", "BackpackUI.html");
        if (!File.Exists(target))
        {
            if (log != null) log.LogWarning("[ContainerPicker] BackpackUI page was not found; container picker stays disabled.");
            return false;
        }
        var original = File.ReadAllBytes(target);
        var utf8 = new UTF8Encoding(false, true);
        var html = utf8.GetString(original);
        if (html.IndexOf("</body>", StringComparison.Ordinal) < 0 || !html.Contains("homebutler-container-ui-v120"))
        {
            if (log != null)
                log.LogWarning("[ContainerPicker] HomeButler BackpackUI page was not detected; container picker stays disabled. Install HomeButler 2.0.0 first.");
            return false;
        }

        var script = ReadScript();
        var scriptTag = "<script id=\"" + Marker + "\">\n" + script + "\n</script>";
        var openTag = "<script id=\"" + Marker + "\">";
        var markerStart = html.IndexOf(openTag, StringComparison.Ordinal);
        string updated;
        if (markerStart >= 0)
        {
            var markerEnd = html.IndexOf("</script>", markerStart, StringComparison.Ordinal);
            if (markerEnd < 0) throw new InvalidDataException("Existing picker script is incomplete.");
            markerEnd += "</script>".Length;
            updated = html.Substring(0, markerStart) + scriptTag + html.Substring(markerEnd);
        }
        else updated = html.Replace("</body>", scriptTag + "\n</body>");

        const string tabDropTag = "<script src=\"../../webui-tabdrop.js\"></script>";
        const string bridgeMarker = "homebutler-container-picker-tabdrop-v1";
        var bridgeTag = "<script id=\"" + bridgeMarker + "\">\n" + ReadTabDropBridge() + "\n</script>";
        if (updated.IndexOf(bridgeMarker, StringComparison.Ordinal) < 0)
        {
            var tabDropAt = updated.IndexOf(tabDropTag, StringComparison.Ordinal);
            if (tabDropAt < 0) throw new InvalidDataException("BackpackUI tab-drop script was not found; drag-to-picker support was not installed.");
            tabDropAt += tabDropTag.Length;
            updated = updated.Insert(tabDropAt, "\n" + bridgeTag);
        }
        updated = UpsertBodyScript(updated, "homebutler-auto-classify-v1", ReadAutoClassifyScript());
        var bytes = utf8.GetBytes(updated);
        var backupDir = Path.Combine(gameRootPath, "BepInEx", "ContainerPicker-backups");
        Directory.CreateDirectory(backupDir);
        var hash = Hash(original);
        var backup = Path.Combine(backupDir, "BackpackUI-" + hash + ".original");
        var checksum = backup + ".sha256";
        if (File.Exists(backup) && Hash(File.ReadAllBytes(backup)) != File.ReadAllText(checksum).Trim())
            throw new IOException("Existing page backup failed its hash check.");
        if (!File.Exists(backup))
        {
            WriteAtomic(backup, original);
            File.WriteAllText(checksum, hash, Encoding.ASCII);
        }
        if (Hash(File.ReadAllBytes(target)) != hash)
            throw new IOException("BackpackUI changed while the picker was being installed.");
        WriteAtomic(target, bytes);
        InstallToolTableRecipeRightClick(log, utf8, gameRootPath, backupDir);
        if (log != null)
            log.LogInfo("[ContainerPicker] Dropdown, native tab-drop targets, and native owner-based auto-classify UI installed.");
        return true;
    }

    private static void InstallToolTableRecipeRightClick(ManualLogSource log, UTF8Encoding utf8, string gameRootPath, string backupDir)
    {
        var target = Path.Combine(gameRootPath, "SurvivalLog_Data", "StreamingAssets", "WebUI", "UI", "ToolTable", "ToolTable.html");
        if (!File.Exists(target)) return;
        var original = File.ReadAllBytes(target);
        var html = utf8.GetString(original);
        if (!html.Contains("id=\"recipe-list\"") || !html.Contains("DYE_PICK")
            || !html.Contains("homebutler-container-ui-v120") || html.IndexOf("</body>", StringComparison.Ordinal) < 0)
        {
            if (log != null)
                log.LogWarning("[ContainerPicker] Expected HomeButler ToolTable recipe and dye UI was not found; right-click support stays disabled.");
            return;
        }

        const string marker = "homebutler-tooltable-recipe-rightclick-v1";
        var source = ReadToolTableRecipeRightClickScript();
        var updated = UpsertBodyScript(html, marker, source);
        if (updated == html) return;

        var hash = Hash(original);
        var backup = Path.Combine(backupDir, "ToolTable-" + hash + ".original");
        var checksum = backup + ".sha256";
        if (File.Exists(backup) && (!File.Exists(checksum) || Hash(File.ReadAllBytes(backup)) != File.ReadAllText(checksum).Trim()))
            throw new IOException("Existing ToolTable backup failed its hash check.");
        if (!File.Exists(backup))
        {
            WriteAtomic(backup, original);
            File.WriteAllText(checksum, hash, Encoding.ASCII);
        }
        if (Hash(File.ReadAllBytes(target)) != hash)
            throw new IOException("ToolTable changed while right-click support was being installed.");
        WriteAtomic(target, utf8.GetBytes(updated));
    }

    private static string ReadScript()
    {
        using (var stream = typeof(ContainerPickerInstaller).Assembly.GetManifestResourceStream("ContainerPicker.js"))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            return reader.ReadToEnd();
    }

    private static string ReadTabDropBridge()
    {
        using (var stream = typeof(ContainerPickerInstaller).Assembly.GetManifestResourceStream("ContainerPickerTabDropBridge.js"))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            return reader.ReadToEnd();
    }

    private static string ReadAutoClassifyScript()
    {
        using (var stream = typeof(ContainerPickerInstaller).Assembly.GetManifestResourceStream("AutoClassify.js"))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            return reader.ReadToEnd();
    }

    private static string ReadToolTableRecipeRightClickScript()
    {
        using (var stream = typeof(ContainerPickerInstaller).Assembly.GetManifestResourceStream("ToolTableRecipeRightClick.js"))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            return reader.ReadToEnd();
    }

    private static string UpsertBodyScript(string html, string id, string source)
    {
        var openTag = "<script id=\"" + id + "\">";
        var scriptTag = openTag + "\n" + source + "\n</script>";
        var start = html.IndexOf(openTag, StringComparison.Ordinal);
        if (start >= 0)
        {
            var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
            if (end < 0) throw new InvalidDataException("Existing script " + id + " is incomplete.");
            end += "</script>".Length;
            return html.Substring(0, start) + scriptTag + html.Substring(end);
        }
        var body = html.IndexOf("</body>", StringComparison.Ordinal);
        if (body < 0) throw new InvalidDataException("BackpackUI closing body tag was not found.");
        return html.Insert(body, scriptTag + "\n");
    }

    private static string Hash(byte[] bytes)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temp = path + ".containerpicker.tmp";
        File.WriteAllBytes(temp, bytes);
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }
}

// HomeButler stores labels by furniture instance id. The scene head bar receives
// the same live Furniture component through its target transform, so apply the
// catalog label at the game's final display-name boundary.
[HarmonyPatch]
internal static class SceneContainerNamePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(FurnitureHeadBar), "SetDisplayName", new[] { typeof(string) });
    }

    private static bool Prefix(FurnitureHeadBar __instance, ref string display)
    {
        try
        {
            if (__instance == null || __instance.Target == null) return true;
            var furniture = __instance.Target.GetComponent<Furniture>();
            if (furniture == null) return true;

            var homeAccess = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "BaseButler.HomeAccess");
            var catalogType = homeAccess?.GetType("ContainerCatalog");
            var find = catalogType == null ? null : AccessTools.Method(catalogType, "Find");
            if (find == null) return true;

            var label = find.Invoke(null, new object[] { ((BaseEntity)furniture).InstanceId, furniture.AgentConfigId });
            if (label == null) return true;
            var labelType = label.GetType();
            var name = labelType.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(label) as string;
            if (string.IsNullOrWhiteSpace(name)) return true;
            var rank = labelType.GetProperty("Rank", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(label);
            display = rank is int value ? "[" + value + "] " + name : name;
        }
        catch (Exception ex)
        {
            ModLog.Error("Scene name lookup failed: " + ex.Message);
        }
        return true;
    }
}
