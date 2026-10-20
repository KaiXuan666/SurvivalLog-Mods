using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using GameCore.HotUpdate.ReduxUI;
using BepInEx;
using BepInEx.Unity.IL2CPP;

[BepInPlugin("local.survivallog.containerinlineeditor", "Current Container Editor", "1.1.1")]
[BepInDependency("local.survivallog.homeaccess")]
public sealed class InlineEditorPlugin : BasePlugin
{
    public override void Load()
    {
        new Harmony("local.survivallog.containerinlineeditor").PatchAll(typeof(InlineEditorPlugin).Assembly);
        try {
            using var stream = typeof(InlineEditorPlugin).Assembly.GetManifestResourceStream("InlineEditor.js");
            using var reader = new StreamReader(stream);
            var script = reader.ReadToEnd();
            var target = Path.Combine(Paths.GameRootPath,"SurvivalLog_Data/StreamingAssets/WebUI/UI/BackpackUI/BackpackUI.html");
            var bytes = File.ReadAllBytes(target);
            var text = new UTF8Encoding(false,true).GetString(bytes);
            const string marker = "homebutler-inline-editor-v111";
            var clean = Regex.Replace(text,"<script id=\"homebutler-inline-editor-v(?:100|110|111)\">[\\s\\S]*?</script>\\r?\\n?", "");
            const string bridge = "window.__hbRightBag = bagB; /* hb-right-state-v111 */";
            if(!clean.Contains(bridge)) {
                const string anchor = "const bagB = makeBagState();";
                if(clean.IndexOf(anchor,StringComparison.Ordinal)<0 || clean.IndexOf(anchor,StringComparison.Ordinal)!=clean.LastIndexOf(anchor,StringComparison.Ordinal))
                    throw new InvalidDataException("Right bag state anchor missing or ambiguous");
                clean=clean.Replace(anchor,anchor+"\n                "+bridge);
            }
            if(!text.Contains("homebutler-container-ui-v120") || !text.Contains("</body>")) throw new InvalidDataException("Container UI dependency missing");
            var result = clean.Replace("</body>","<script id=\""+marker+"\">\n"+script+"\n</script>\n</body>");
            if(result==text)return;
            var backup = Path.Combine(Paths.GameRootPath,"BepInEx/ContainerInlineEditor-backups");
            Directory.CreateDirectory(backup);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            var original = Path.Combine(backup,hash+".html");
            if(!File.Exists(original)) File.WriteAllBytes(original,bytes);
            if(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(target)))!=hash) throw new IOException("Page changed during patching");
            File.WriteAllText(target+".inline.tmp",result,new UTF8Encoding(false));
            File.Move(target+".inline.tmp",target,true);
            Log.LogInfo("[ContainerInlineEditor] Backpack container editor installed");
        } catch(Exception e) { Log.LogError("[ContainerInlineEditor] "+e.Message); }
    }
}

// Vuplex forwards IME composition; Unity must allow the OS input method first.
[HarmonyPatch(typeof(WebUILayer), nameof(WebUILayer.SetKeyboardEnabled))]
internal static class InlineIme
{
    static bool changed;
    static IMECompositionMode previous;
    static bool auto;
    static void Postfix(bool __0)
    {
        if(__0) {
            if(!changed) { previous=Input.imeCompositionMode; auto=Vuplex.WebView.Web.AutoEnableIme; changed=true; }
            Vuplex.WebView.Web.AutoEnableIme=true;
            Input.imeCompositionMode=IMECompositionMode.On;
        } else if(changed) {
            Input.imeCompositionMode=previous;
            Vuplex.WebView.Web.AutoEnableIme=auto;
            changed=false;
        }
    }
}
