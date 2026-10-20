using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

// Shared by the plugin and installer: no game API or binary offsets.
public static class AdaptiveLayout
{
    public const string RelativePath = "SurvivalLog_Data/StreamingAssets/WebUI/UI/BackpackUI/BackpackUI.html";
    public const string Marker = "homeaccess-adaptive-layout-v1";
    static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    static string Once(string text, string pattern, string replacement, int count)
    {
        var regex = new Regex(pattern);
        if (regex.Matches(text).Count != count)
            throw new InvalidOperationException("Unsupported BackpackUI structure: " + pattern);
        return regex.Replace(text, replacement);
    }
    public static string Transform(string text)
    {
        if (text.Contains(Marker)) return text;
        if (text.Contains("homeaccess-container-tabs"))
            throw new InvalidOperationException("Legacy UI patch found; restore the matching original UI first.");
        // Validate every anchor before returning a transformed document.
        text = Once(text, "bag-tabs unity-interactive", "bag-tabs bag-tabs-scroll unity-interactive", 2);
        text = Once(text, @"for\s*\(const t of aTabs\.value\)\s*if\s*\(t\.cols > maxCols\)\s*maxCols = t\.cols;",
            "/* HomeAccess: size to the selected container. */", 1);
        text = Once(text, @"for\s*\(const t of aTabs\.value\)\s*if\s*\(t\.rows > maxRows\)\s*maxRows = t\.rows;",
            "/* HomeAccess: size to the selected container. */", 1);
        text = Once(text, @"const FIT_MARGIN\s*=\s*24;", "const FIT_MARGIN = 72;", 1);
        text = Once(text, "window.addEventListener\\('mousemove', onMouseMove\\);", Script + "\nwindow.addEventListener('mousemove', onMouseMove);", 1);
        text = Once(text, "</head>", Style + "\n</head>", 1);
        return text;
    }
    const string Style = @"<style id=""homeaccess-adaptive-layout-v1"">
.bag-tabs::-webkit-scrollbar { height:9px; }
.bag-tabs::-webkit-scrollbar-thumb { background:#8f835f; border-radius:5px; }
.bag-tabs::-webkit-scrollbar-track { background:rgba(0,0,0,.25); }
.bag-slot.is-anchored .header { min-width:0; }
.bag-slot.is-anchored .bag-tabs { width:0; min-width:0; max-width:100%; overflow-x:auto; overflow-y:hidden; scrollbar-width:thin; }
.bag-slot.is-anchored .bag-tab { flex:0 0 auto; white-space:nowrap; font-size:clamp(14px,1.2vw,22px); }
</style>";
    const string Script = @"
                watch(() => [curATab.value, aTabs.value.length], () => nextTick(() => {
                    const bar = containerEl && containerEl.querySelector('.bag-tabs');
                    const tab = bar && bar.querySelectorAll('.bag-tab')[curATab.value];
                    if (!tab) return;
                    if (tab.offsetLeft < bar.scrollLeft) bar.scrollLeft = tab.offsetLeft;
                    else if (tab.offsetLeft + tab.offsetWidth > bar.scrollLeft + bar.clientWidth)
                        bar.scrollLeft = tab.offsetLeft + tab.offsetWidth - bar.clientWidth;
                }));
                const onHomeTabsScroll = (event) => {
                    if (event.target && event.target.classList && event.target.classList.contains('bag-tabs') && tabDrop.isArmed()) tabDrop.arm();
                };
                onMounted(() => document.addEventListener('scroll', onHomeTabsScroll, true));
                onUnmounted(() => document.removeEventListener('scroll', onHomeTabsScroll, true));
";
    public static string Hash(byte[] bytes)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }
    public static string Safe(string root, string relative)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, relative));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path outside game root");
        for (var cursor = path; cursor != null && cursor.Length >= prefix.Length - 1; cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked paths are not supported: " + cursor);
        return path;
    }
    public static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static string Apply(string root, bool checkOnly)
    {
        var path = Safe(root, RelativePath);
        var before = File.ReadAllBytes(path);
        var text = Utf8.GetString(before);
        if (text.Contains(Marker)) return "Layout already applied.";
        var helper = File.ReadAllText(Safe(root, "SurvivalLog_Data/StreamingAssets/WebUI/webui-tabdrop.js"));
        if (!helper.Contains("clipRect") || !helper.Contains(".bag-tabs-scroll") || !helper.Contains("isArmed"))
            throw new InvalidOperationException("Native tab clipping helper changed; layout left untouched.");
        var after = Utf8.GetBytes(Transform(text));
        if (checkOnly) return "Layout structure supported.";
        var backup = Safe(root, "BepInEx/HomeButler-layout-backups/" + Hash(after) + ".original");
        var checksum = backup + ".sha256";
        // Back up exact current-version bytes, never a bundled old game page.
        if (File.Exists(backup)) {
            if (Hash(File.ReadAllBytes(backup)) != Hash(before)) throw new IOException("Layout backup conflict");
        } else AtomicWrite(backup, before);
        AtomicWrite(checksum, Encoding.ASCII.GetBytes(Hash(before)));
        if (Hash(File.ReadAllBytes(path)) != Hash(before)) throw new IOException("UI changed while patching");
        AtomicWrite(path, after);
        return "Adaptive layout applied; original saved as " + Path.GetFileName(backup);
    }
    public static string Restore(string root, bool checkOnly)
    {
        var path = Safe(root, RelativePath);
        var current = File.ReadAllBytes(path);
        if (!Utf8.GetString(current).Contains(Marker)) return "Current game UI is unpatched; preserved.";
        var backup = Safe(root, "BepInEx/HomeButler-layout-backups/" + Hash(current) + ".original");
        if (!File.Exists(backup) || !File.Exists(backup + ".sha256"))
            throw new IOException("UI changed or backup missing; refusing to restore an old game page.");
        var original = File.ReadAllBytes(backup);
        if (Hash(original) != File.ReadAllText(backup + ".sha256")) throw new IOException("Layout backup damaged");
        if (!checkOnly) AtomicWrite(path, original);
        return "Current-version original UI restored.";
    }
}
