using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;
using GameCore.HotUpdate.Battle.Show;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.UI;

namespace SurvivalLog.Mods
{
    // HomeButler 只把自定义容器名存在自己的目录 JSON 里，游戏的名称栏（长按 Alt 显示）
    // 仍然读家具配置里的原名。这里在名称栏的渲染边界把两者接上：
    // 家具在目录里有自定义名时，用「[序号] 名称」覆盖名称栏文本与它的 _baseName，
    // 这样游戏自己后续的重排（搜索计数等）也会沿用自定义名。
    internal static class ContainerLabelStore
    {
        private static bool initialized;
        private static bool ready;
        private static bool missedLogged;
        private static float nextAttempt;
        private static FieldInfo catalogField;
        private static FieldInfo loadedIdsField;
        private static PropertyInfo labelsProperty;
        private static PropertyInfo nameProperty;
        private static PropertyInfo rankProperty;
        private static PropertyInfo furnitureProperty;

        internal static bool Ready
        {
            get { Ensure(); return ready; }
        }

        private static void Ensure()
        {
            // HomeButler 是软依赖，可能比本插件晚就绪：未绑定时每 2 秒重试一次，
            // 失败只记一次日志，避免刷屏。
            if (ready) return;
            if (initialized && Time.realtimeSinceStartup < nextAttempt) return;
            initialized = true;
            nextAttempt = Time.realtimeSinceStartup + 2f;
            try
            {
                Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "BaseButler.HomeAccess");
                if (assembly == null)
                {
                    if (!missedLogged)
                    {
                        missedLogged = true;
                        ModLog.Info("[SceneNames] HomeButler is not loaded yet; scene names stay native until it appears.");
                    }
                    return;
                }
                Type catalogType = assembly.GetType("ContainerCatalog");
                if (catalogType == null) return;

                catalogField = AccessTools.Field(catalogType, "data");
                loadedIdsField = AccessTools.Field(catalogType, "loadedIds");
                if (catalogField == null || loadedIdsField == null) return;

                labelsProperty = catalogField.FieldType.GetProperty("Labels");
                MethodInfo find = AccessTools.Method(catalogType, "Find");
                Type labelType = find == null ? null : find.ReturnType;
                if (labelsProperty == null || labelType == null) return;

                nameProperty = labelType.GetProperty("Name");
                rankProperty = labelType.GetProperty("Rank");
                furnitureProperty = labelType.GetProperty("Furniture");
                ready = nameProperty != null;
            }
            catch (Exception ex)
            {
                ModLog.Warning("[SceneNames] Container catalog could not be bound: " + ex.Message);
                ready = false;
            }
        }

        // 返回名称栏应显示的文本；没有自定义名称时返回 false，保留游戏原名。
        internal static bool TryResolve(long owner, int config, out string display, out string reason)
        {
            display = null;
            reason = null;
            Ensure();
            if (!ready) { reason = "container catalog unavailable"; return false; }
            try
            {
                object data = catalogField.GetValue(null);
                if (data == null) { reason = "catalog not loaded"; return false; }
                IDictionary labels = labelsProperty.GetValue(data) as IDictionary;
                if (labels == null || labels.Count == 0) { reason = "catalog has no labels"; return false; }

                // 目录按存档内的稳定实例 id 存；场景里的家具用运行时实例 id，
                // loadedIds 就是这两者之间的映射（BaseAgent.LoadGame 时写入）。
                long stable = owner;
                if (loadedIdsField.GetValue(null) is IDictionary map && map.Contains(owner))
                {
                    long mapped = Convert.ToInt64(map[owner]);
                    if (mapped != 0) stable = mapped;
                }

                object label = null;
                string key = stable.ToString();
                if (labels.Contains(key)) label = labels[key];
                else if (stable != owner && labels.Contains(owner.ToString())) label = labels[owner.ToString()];
                if (label == null) { reason = "no label for owner " + owner + " (stable " + stable + ")"; return false; }

                string name = nameProperty.GetValue(label) as string;
                if (string.IsNullOrWhiteSpace(name)) { reason = "label is empty"; return false; }

                if (furnitureProperty != null)
                {
                    object stored = furnitureProperty.GetValue(label);
                    int storedConfig = stored == null ? config : Convert.ToInt32(stored);
                    if (storedConfig != config)
                    {
                        // 同一实例 id 换了家具类型时不能沿用旧名。
                        reason = "label belongs to config " + storedConfig + ", scene has " + config;
                        return false;
                    }
                }

                object rank = rankProperty == null ? null : rankProperty.GetValue(label);
                display = rank is int value ? "[" + value + "] " + name : name;
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }
    }

    internal static class SceneContainerNames
    {
        private static readonly HashSet<string> logged = new HashSet<string>();
        private static readonly Dictionary<IntPtr, BarState> states = new Dictionary<IntPtr, BarState>();
        private static readonly PropertyInfo showInstanceId =
            AccessTools.Property(typeof(BaseHeadBar), "showInstanceId");
        private static bool installTried;
        private static int missLogs;

        /// <summary>
        /// 逐个方法手动打补丁：任一目标不存在时只记录日志，绝不连带影响插件其他部分。
        /// </summary>
        internal static void Install(Harmony harmony, ManualLogSource log)
        {
            if (installTried) return;
            installTried = true;
            if (log == null) return;

            TryPatch(harmony, log, typeof(FurnitureHeadBar), "SetName",
                nameof(OnNameAssigned), new[] { typeof(string) });
            TryPatch(harmony, log, typeof(FurnitureHeadBar), "SetDisplayName",
                nameof(OnNameAssigned), new[] { typeof(string) });
            TryPatch(harmony, log, typeof(FurnitureHeadBar), "ApplyNameWithSearchCount",
                nameof(OnNameComposed), Type.EmptyTypes);
            TryPatch(harmony, log, typeof(FurnitureHeadBar), "OnUpdate",
                nameof(OnBarUpdated), new[] { typeof(float) });
        }

        private static void TryPatch(Harmony harmony, ManualLogSource log, Type type, string name,
            string postfixName, Type[] arguments)
        {
            try
            {
                MethodInfo target = AccessTools.Method(type, name, arguments);
                if (target == null)
                {
                    log.LogWarning("[SceneNames] " + type.Name + "." + name + " was not found; that path stays native.");
                    return;
                }
                MethodInfo postfix = AccessTools.Method(typeof(SceneContainerNames), postfixName);
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                log.LogInfo("[SceneNames] Hooked " + type.Name + "." + name + ".");
            }
            catch (Exception ex)
            {
                log.LogWarning("[SceneNames] Could not hook " + type.Name + "." + name + ": " + ex.Message);
            }
        }

        // 游戏给名称栏赋值时（SetName / SetDisplayName）：gameName 是游戏自己的名字，
        // 用来把「名称 + 搜索计数」这类组合文本里的名称部分替换掉。
        private static void OnNameAssigned(FurnitureHeadBar __instance, string __0)
        {
            Apply(__instance, __0, "SetName");
        }

        private static void OnNameComposed(FurnitureHeadBar __instance)
        {
            Apply(__instance, null, "ApplyName");
        }

        private static void OnBarUpdated(FurnitureHeadBar __instance, float deltaTime)
        {
            if (__instance == null) return;
            // 名称栏每帧都会重排；缓存命中时只回写文本（不碰目录），
            // 每 16 帧重新解析一次，改名后最多 0.3 秒就跟上。
            IntPtr key = __instance.Pointer;
            int frame = Time.frameCount;
            BarState state;
            if (states.TryGetValue(key, out state) && frame < state.NextResolveFrame)
            {
                if (state.Display != null) Write(__instance, state.Display, null);
                return;
            }
            Apply(__instance, null, "OnUpdate");
        }

        private static void Apply(FurnitureHeadBar bar, string gameName, string source)
        {
            if (bar == null) return;
            IntPtr key = bar.Pointer;
            try
            {
                Furniture furniture = FindFurniture(bar);
                if (furniture == null)
                {
                    states[key] = new BarState { NextResolveFrame = Time.frameCount + 120 };
                    LogOnce(source + ":no-furniture", "[SceneNames] " + source + ": head bar target has no Furniture component.");
                    return;
                }

                long owner = ((BaseEntity)furniture).InstanceId;
                int config = furniture.AgentConfigId;
                if (!ContainerLabelStore.TryResolve(owner, config, out string display, out string reason))
                {
                    // 绝大多数家具本来就没有自定义名，只记前几条未命中，避免刷日志。
                    states[key] = new BarState { NextResolveFrame = Time.frameCount + 120 };
                    if (missLogs < 5)
                    {
                        missLogs++;
                        LogOnce(source + ":miss:" + owner, "[SceneNames] " + source + ": owner=" + owner
                            + " config=" + config + " -> " + reason);
                    }
                    return;
                }

                states[key] = new BarState
                {
                    Owner = owner,
                    Display = display,
                    NextResolveFrame = Time.frameCount + 16
                };
                if (states.Count > 512) states.Clear();

                string before = bar.Text_Name == null ? null : bar.Text_Name.text;
                Write(bar, display, gameName);

                LogOnce("hit:" + owner, "[SceneNames] " + source + ": owner=" + owner + " config=" + config
                    + " -> \"" + display + "\" (" + before + " -> " + (bar.Text_Name == null ? "" : bar.Text_Name.text) + ")");
            }
            catch (Exception ex)
            {
                states[key] = new BarState { NextResolveFrame = Time.frameCount + 120 };
                LogOnce("error:" + source, "[SceneNames] " + source + " failed: " + ex);
            }
        }

        // 只负责把名称写进名称栏；gameName 是游戏原本要显示的名字，
        // 用来把「名称 + 搜索计数」这类组合文本里的名称部分替换掉。
        private static void Write(FurnitureHeadBar bar, string display, string gameName)
        {
            Text text = bar.Text_Name;
            string current = text == null ? null : text.text;
            string baseName = gameName;
            if (string.IsNullOrEmpty(baseName)) baseName = bar._baseName;

            string composed = display;
            if (!string.IsNullOrEmpty(current) && !string.IsNullOrEmpty(baseName)
                && current.Length > baseName.Length
                && current.StartsWith(baseName, StringComparison.Ordinal))
            {
                // 保留游戏追加的计数等后缀，只替换名称部分。
                composed = display + current.Substring(baseName.Length);
            }

            if (!string.Equals(bar._baseName, display, StringComparison.Ordinal)) bar._baseName = display;
            if (text != null && !string.Equals(current, composed, StringComparison.Ordinal)) text.text = composed;
        }

        private sealed class BarState
        {
            internal long Owner;
            internal string Display;
            internal int NextResolveFrame;
        }

        private static Furniture FindFurniture(FurnitureHeadBar bar)
        {
            Transform target = bar.Target;
            if (target != null)
            {
                Transform node = target;
                for (int depth = 0; depth < 8 && node != null; depth++)
                {
                    Furniture direct = node.GetComponent<Furniture>();
                    if (direct != null) return direct;
                    node = node.parent;
                }
                int children = target.childCount;
                for (int i = 0; i < children; i++)
                {
                    Furniture nested = target.GetChild(i).GetComponent<Furniture>();
                    if (nested != null) return nested;
                }
            }

            // 名称栏挂在模型子节点上时，退回到它记录的实例 id 去逻辑层取家具。
            long id = ShowInstanceId(bar);
            if (id == 0) return null;
            BattleLogicWorld world = BaseSingleton<BattleLogicWorld>._Instance;
            AgentManager agents = world == null ? null : world._AgentManager;
            BaseAgent agent = agents == null ? null : agents.GetAgent(id);
            return agent == null ? null : ((Il2CppObjectBase)(object)agent).TryCast<Furniture>();
        }

        private static long ShowInstanceId(BaseHeadBar bar)
        {
            try
            {
                if (showInstanceId == null) return 0;
                object value = showInstanceId.GetValue(bar);
                return value == null ? 0 : Convert.ToInt64(value);
            }
            catch { return 0; }
        }

        private static void LogOnce(string key, string message)
        {
            if (!logged.Add(key)) return;
            if (logged.Count > 200) logged.Clear();
            ModLog.Info(message);
        }
    }
}
