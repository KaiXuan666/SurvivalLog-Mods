using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Logging;
using GameCore.HotUpdate.ReduxUI;

namespace SurvivalLog.Butler
{
    // 游戏主界面（标题菜单）右侧栏的模组按钮与设置面板。
    //
    // 做法与容器下拉一致：把脚本注入到 MainUI.html，页面点击后发 SL_BUTLER_* 消息回 Unity，
    // 由 ButlerMenuBridge 读写设置并把结果广播回页面。
    // 页面只负责外观与交互，设置的真实来源始终是 BepInEx 的 cfg 文件。
    //
    // 选 MainUI 而不是游戏内 HUD（CoreUI0）：那里是编译后的 Vue render，
    // 往里塞节点会被下一次 patch 覆盖；而 MainUI 的右侧栏是静态 HTML，
    // 且已有 .side-btn 样式与 animateSideClick，按钮能直接沿用游戏的观感与点击反馈。
    internal static class ButlerMenu
    {
        internal const string Marker = "survival-butler-menu-v1";
        private const string PageRelativePath = "SurvivalLog_Data/StreamingAssets/WebUI/UI/MainUI/MainUI.html";

        // 用来确认「这是主界面」的标记：右侧栏及其按钮样式、点击反馈函数。
        private static readonly string[] RequiredPageTokens = { "class=\"sidebar\"", ".side-btn", "animateSideClick" };

        internal static bool TryInstall(ManualLogSource log, string gameRootPath)
        {
            string target = Path.Combine(gameRootPath, PageRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(target))
            {
                Warning(log, "Main menu page (MainUI.html) was not found; mod menu button stays disabled.");
                return false;
            }

            byte[] original = File.ReadAllBytes(target);
            var utf8 = new UTF8Encoding(false, true);
            string html;
            try { html = utf8.GetString(original); }
            catch (DecoderFallbackException)
            {
                Warning(log, "Main menu page is not valid UTF-8; mod menu button stays disabled.");
                return false;
            }

            foreach (string token in RequiredPageTokens)
            {
                if (html.IndexOf(token, StringComparison.Ordinal) < 0)
                {
                    Warning(log, "Main menu layout changed (missing \"" + token + "\"); mod menu button stays disabled.");
                    return false;
                }
            }
            if (html.IndexOf("</body>", StringComparison.Ordinal) < 0)
            {
                Warning(log, "Main menu page has no closing body tag; mod menu button stays disabled.");
                return false;
            }

            string script = ReadScript();
            string updated = UpsertBodyScript(html, Marker, script);
            if (updated == html)
            {
                ModLog.Info("[ButlerMenu] Mod menu script already up to date.");
                return true;
            }

            Backup(log, gameRootPath, original);
            WriteAtomic(target, utf8.GetBytes(updated));
            ModLog.Info("[ButlerMenu] Mod menu button installed on the main menu sidebar.");
            return true;
        }

        private static void Warning(ManualLogSource log, string message)
        {
            if (log != null) log.LogWarning("[ButlerMenu] " + message);
        }

        private static string ReadScript()
        {
            using (Stream stream = typeof(ButlerMenu).Assembly.GetManifestResourceStream("ButlerMenu.js"))
            {
                if (stream == null) throw new InvalidOperationException("Embedded resource ButlerMenu.js is missing.");
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        private static string UpsertBodyScript(string html, string id, string source)
        {
            string openTag = "<script id=\"" + id + "\">";
            string scriptTag = openTag + "\n" + source + "\n</script>";
            int start = html.IndexOf(openTag, StringComparison.Ordinal);
            if (start >= 0)
            {
                int end = html.IndexOf("</script>", start, StringComparison.Ordinal);
                if (end < 0) throw new InvalidDataException("Existing script " + id + " is incomplete.");
                end += "</script>".Length;
                string replaced = html.Substring(0, start) + scriptTag + html.Substring(end);
                return replaced == html ? html : replaced;
            }
            int body = html.IndexOf("</body>", StringComparison.Ordinal);
            return html.Insert(body, scriptTag + "\n");
        }

        private static void Backup(ManualLogSource log, string gameRootPath, byte[] original)
        {
            string backupDir = Path.Combine(gameRootPath, "BepInEx", "Butler-menu-backups");
            Directory.CreateDirectory(backupDir);
            string hash = Sha256(original);
            string backup = Path.Combine(backupDir, "MainUI-" + hash + ".original");
            string checksum = backup + ".sha256";
            if (File.Exists(backup) && File.Exists(checksum) && Sha256(File.ReadAllBytes(backup)) != File.ReadAllText(checksum).Trim())
                throw new IOException("Existing main menu backup failed its hash check.");
            if (!File.Exists(backup))
            {
                WriteAtomic(backup, original);
                File.WriteAllText(checksum, hash, Encoding.ASCII);
            }
            else if (log != null) log.LogInfo("[ButlerMenu] Original main menu already backed up.");
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }

        private static void WriteAtomic(string path, byte[] bytes)
        {
            string temp = path + ".butlermenu.tmp";
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
    }

    // 页面消息 -> 设置读写。调用方是 ButlerMenuMessagePatch。
    internal static class ButlerMenuBridge
    {
        internal static void Handle(WebUILayer layer, string messageType, string jsonData)
        {
            switch (messageType)
            {
                // 页面刚加载完，要一份当前设置来渲染面板。
                case "SL_BUTLER_QUERY":
                    Broadcast(layer, BuildStateJson(true));
                    break;

                // 面板里的每一处改动都整份提交，避免逐字段同步时的次序问题。
                case "SL_BUTLER_APPLY":
                    Apply(jsonData);
                    // 回传 applied=true 让页面知道已经落盘，可以收起「未保存」提示。
                    Broadcast(layer, BuildStateJson(true));
                    break;
            }
        }

        private static void Apply(string jsonData)
        {
            if (string.IsNullOrEmpty(jsonData)) return;
            bool enabled;
            if (TryReadBool(jsonData, "enabled", out enabled)) ButlerSettings.EnhanceEnabled = enabled;

            float value;
            if (TryReadNumber(jsonData, "speed", out value)) ButlerSettings.MoveSpeedMultiplier = value;
            if (TryReadNumber(jsonData, "exposure", out value)) ButlerSettings.ExposureMultiplier = value;

            ModLog.Info("[ButlerMenu] Settings applied: enabled=" + ButlerSettings.EnhanceEnabled
                + " speed=x" + ButlerSettings.MoveSpeedMultiplier.ToString("0.##", CultureInfo.InvariantCulture)
                + " exposure=x" + ButlerSettings.ExposureMultiplier.ToString("0.##", CultureInfo.InvariantCulture) + ".");
        }

        private static void Broadcast(WebUILayer layer, string json)
        {
            if (layer == null) return;
            layer.BroadcastToAllPages("SL_BUTLER_STATE", json);
        }

        internal static string BuildStateJson(bool applied)
        {
            var sb = new StringBuilder(192);
            sb.Append('{');
            sb.Append("\"applied\":").Append(applied ? "true" : "false").Append(',');
            sb.Append("\"enabled\":").Append(ButlerSettings.EnhanceEnabled ? "true" : "false").Append(',');
            sb.Append("\"speed\":").Append(Num(ButlerSettings.MoveSpeedMultiplier)).Append(',');
            sb.Append("\"exposure\":").Append(Num(ButlerSettings.ExposureMultiplier)).Append(',');
            sb.Append("\"min\":").Append(Num(ButlerSettings.MinMultiplier)).Append(',');
            sb.Append("\"max\":").Append(Num(ButlerSettings.MaxMultiplier));
            sb.Append('}');
            return sb.ToString();
        }

        private static string Num(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        // 手写极简 JSON 取值：设置里的字段是固定的几个标量，不值得为它引入依赖。
        // 数值用 InvariantCulture 解析，避免某些区域设置把小数点当成分组分隔符。
        private static bool TryReadBool(string json, string key, out bool value)
        {
            value = false;
            string raw = ReadRawToken(json, key);
            if (raw == null) return false;
            if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
            if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
            return false;
        }

        private static bool TryReadNumber(string json, string key, out float value)
        {
            value = 0f;
            string raw = ReadRawToken(json, key);
            if (raw == null) return false;
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string ReadRawToken(string json, string key)
        {
            string needle = "\"" + key + "\"";
            int at = json.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) return null;
            int colon = json.IndexOf(':', at + needle.Length);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length) return null;
            if (json[i] == '"')
            {
                int endQuote = json.IndexOf('"', i + 1);
                return endQuote < 0 ? null : json.Substring(i + 1, endQuote - i - 1);
            }
            int stop = i;
            while (stop < json.Length && json[stop] != ',' && json[stop] != '}') stop++;
            return json.Substring(i, stop - i).Trim();
        }
    }
}
