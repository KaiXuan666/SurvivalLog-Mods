using BepInEx.Logging;

namespace SurvivalLog.Mods
{
    // 合并后的单一插件里，各功能模块共用一个 BepInEx 日志源。
    internal static class ModLog
    {
        private static ManualLogSource logger;

        internal static void Attach(ManualLogSource source)
        {
            logger = source;
        }

        internal static void Info(string message)
        {
            ManualLogSource current = logger;
            if (current != null) current.LogInfo(message);
        }

        internal static void Warning(string message)
        {
            ManualLogSource current = logger;
            if (current != null) current.LogWarning(message);
        }

        internal static void Error(string message)
        {
            ManualLogSource current = logger;
            if (current != null) current.LogError(message);
        }
    }
}
