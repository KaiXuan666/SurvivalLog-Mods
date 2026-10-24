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
using SurvivalLog.Butler;
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

        // 清理旧的改名功能 (InlineEditor) 与旧的容器排序功能 (Navigation)
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<script id=""homebutler-inline-editor-v[^""]*"">[\s\S]*?</script>\r?\n?", "");
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<script id=""homebutler-navigation-v[^""]*"">[\s\S]*?</script>\r?\n?", "");

        // 彻底废除旧 flex order 排序与旧改名/排序管理弹窗，保持官方原生顺序
        const string resetOrderMarker = "hb-reset-order-style";
        if (!html.Contains(resetOrderMarker))
        {
            var headEnd = html.IndexOf("</head>", StringComparison.Ordinal);
            if (headEnd >= 0)
            {
                const string resetStyle = "<style id=\"" + resetOrderMarker + "\">.bag-tabs .bag-tab, .cabinet-tab { order: initial !important; } .hb-tools { display: none !important; }</style>\n";
                html = html.Insert(headEnd, resetStyle);
            }
        }

        CleanLegacyScripts(gameRootPath);

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
        updated = UpsertBodyScript(updated, "survivallog-container-rename-v1", ReadContainerRenameScript());
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
        updated = PatchToolTableRecipeDynamicFix(updated);
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
        if (log != null)
            log.LogInfo("[ContainerPicker] ToolTable recipe right-click and static/dynamic recipe fix installed.");
    }

    private static string PatchToolTableRecipeDynamicFix(string html)
    {
        if (html.Contains("WebUI_ToolTable_RecipeStaticMsg"))
            return html;

        var recipeMsgPattern = new System.Text.RegularExpressions.Regex(
            @"'WebUI_ToolTable_RecipeMsg':\s*\(data\)\s*=>\s*\{[\s\S]*?renderRecipes\(data\.recipes\);[\s\S]*?\}",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        if (!recipeMsgPattern.IsMatch(html))
            return html;

        const string recipeHandlersReplacement = 
@"'WebUI_ToolTable_RecipeStaticMsg': (data) => {
            if (data && data.recipes) {
                data.recipes.forEach(r => {
                    if (r && r.recipeKey) staticRecipesByKey.set(r.recipeKey, r);
                });
                if (lastRawRecipeMsg) {
                    processRecipeMsg(lastRawRecipeMsg);
                }
            }
        },
        'WebUI_ToolTable_RecipeMsg': (data) => {
            lastRawRecipeMsg = data;
            processRecipeMsg(data);
        }";

        html = recipeMsgPattern.Replace(html, recipeHandlersReplacement, 1);

        var restorePattern = new System.Text.RegularExpressions.Regex(
            @"(let collapsedCatsRestored\s*=\s*false;)",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        if (!restorePattern.IsMatch(html))
            return html;

        const string helperReplacement = 
@"$1
    const staticRecipesByKey = new Map();
    let lastRawRecipeMsg = null;

    function processRecipeMsg(data) {
        if (!data) return;
        const notesSection = document.querySelector('.notes-section');
        const combinedPanel = document.querySelector('.combined-panel');

        if (!collapsedCatsRestored) {
            collapsedCatsRestored = true;
            const collapsedMask = data.collapsedCats || 0;
            NOTE_CAT_META.forEach(meta => {
                if (collapsedMask & (1 << meta.id)) collapsedCats.add(meta.id);
            });
        }
        
        if (data.show) {
            notesSection.style.display = 'flex';
            combinedPanel.classList.remove('no-notes');

            const recipes = data.recipes || [];
            const mergedList = [];
            for (let i = 0; i < recipes.length; i++) {
                const dyn = recipes[i];
                if (!dyn) continue;
                const st = staticRecipesByKey.get(dyn.recipeKey);
                const item = Object.assign({}, st, dyn);

                if (st && st.materialsJson) {
                    try {
                        const mats = JSON.parse(st.materialsJson);
                        const haveList = (dyn.materialsHave !== undefined && dyn.materialsHave !== null)
                            ? String(dyn.materialsHave).split(',')
                            : [];
                        for (let j = 0; j < mats.length; j++) {
                            const have = (haveList[j] !== undefined && haveList[j] !== '') ? parseInt(haveList[j], 10) : 0;
                            mats[j].have = isNaN(have) ? 0 : have;
                            mats[j].hasEnough = mats[j].have >= (mats[j].need || 0);
                        }
                        item.materialsJson = JSON.stringify(mats);
                    } catch (e) {
                        item.materialsJson = st.materialsJson;
                    }
                }
                mergedList.push(item);
            }

            renderRecipes(mergedList);
            renderRecentRecipes(data.recentRecipeKeys);
        } else {
            notesSection.style.display = 'none';
            combinedPanel.classList.add('no-notes');
        }
    }";

        html = restorePattern.Replace(html, helperReplacement, 1);
        return html;
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

    private static string ReadContainerRenameScript()
    {
        using (var stream = typeof(ContainerPickerInstaller).Assembly.GetManifestResourceStream("ContainerRenameUI.js"))
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

    private static void CleanLegacyScripts(string gameRootPath)
    {
        string[] pages = { "BackpackUI", "BrewPanel", "Cooking", "RatCage", "ToolTable", "TradeUI" };
        foreach (var page in pages)
        {
            var target = Path.Combine(gameRootPath, "SurvivalLog_Data", "StreamingAssets", "WebUI", "UI", page, page + ".html");
            if (!File.Exists(target)) continue;
            try
            {
                var text = File.ReadAllText(target, Encoding.UTF8);
                var clean = System.Text.RegularExpressions.Regex.Replace(text, @"<script id=""homebutler-inline-editor-v[^""]*"">[\s\S]*?</script>\r?\n?", "");
                clean = System.Text.RegularExpressions.Regex.Replace(clean, @"<script id=""homebutler-navigation-v[^""]*"">[\s\S]*?</script>\r?\n?", "");
                if (clean != text) File.WriteAllText(target, clean, Encoding.UTF8);
            }
            catch { }
        }
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
