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
        private static MethodInfo findMethod;
        private static FieldInfo catalogField;
        private static PropertyInfo labelsProperty;
        private static PropertyInfo nameProperty;
        private static PropertyInfo rankProperty;

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

                // 直接复用 HomeButler 自己的查找逻辑：它已经处理了
                // 「运行时实例 id -> 存档稳定 id」的映射以及家具类型校验。
                findMethod = AccessTools.Method(catalogType, "Find", new[] { typeof(long), typeof(int) });
                if (findMethod == null) return;
                Type labelType = findMethod.ReturnType;
                nameProperty = labelType.GetProperty("Name");
                rankProperty = labelType.GetProperty("Rank");
                // 直查路径：名称栏记录的 id 本身可能就是存档稳定 id。
                catalogField = AccessTools.Field(catalogType, "data");
                labelsProperty = catalogField == null
                    ? null
                    : catalogField.FieldType.GetProperty("Labels");
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
                object label = findMethod.Invoke(null, new object[] { owner, config });
                if (label == null) { reason = "no label for owner " + owner + " (config " + config + ")"; return false; }

                string name = nameProperty.GetValue(label) as string;
                if (string.IsNullOrWhiteSpace(name)) { reason = "label is empty"; return false; }

                object rank = rankProperty == null ? null : rankProperty.GetValue(label);
                display = rank is int value ? "[" + value + "] " + name : name;
                return true;
            }
            catch (Exception ex)
            {
                reason = (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        // 兜底：直接把名称栏记录的 id 当存档稳定 id 去目录里查（不做家具类型校验）。
        internal static bool TryResolveStable(long stableId, out string display)
        {
            display = null;
            if (stableId == 0) return false;
            Ensure();
            if (!ready || catalogField == null || labelsProperty == null) return false;
            try
            {
                object data = catalogField.GetValue(null);
                if (data == null) return false;
                IDictionary labels = labelsProperty.GetValue(data) as IDictionary;
                if (labels == null) return false;
                object label = labels[stableId.ToString()];
                if (label == null) return false;

                string name = nameProperty.GetValue(label) as string;
                if (string.IsNullOrWhiteSpace(name)) return false;

                object rank = rankProperty == null ? null : rankProperty.GetValue(label);
                display = rank is int value ? "[" + value + "] " + name : name;
                return true;
            }
            catch { return false; }
        }
    }

    internal static class SceneContainerNames
    {
        private static readonly HashSet<string> logged = new HashSet<string>();
        private static readonly Dictionary<IntPtr, BarState> states = new Dictionary<IntPtr, BarState>();
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
            // OnUpdate 实际声明在 BaseHeadBar 上，单位血条也会走到这里；
            // 处理时必须先确认实例真的是家具名称栏，否则字段偏移会读错对象。
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
                log.LogInfo("[SceneNames] Hooked " + type.Name + "." + name
                    + (target.DeclaringType == type ? "." : " (declared on " + target.DeclaringType.Name + ")."));
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

        private static void OnBarUpdated(BaseHeadBar __instance, float deltaTime)
        {
            if (__instance == null) return;
            FurnitureHeadBar bar;
            try { bar = __instance.TryCast<FurnitureHeadBar>(); }
            catch { return; }
            if (bar == null) return;

            // 名称栏每帧都会重排；缓存命中时只回写文本（不碰目录与 Agent 查询）。
            BarState state;
            if (states.TryGetValue(bar.Pointer, out state) && Time.frameCount < state.NextResolveFrame)
            {
                if (state.Display != null) Write(bar, state.Display, null);
                return;
            }
            Apply(bar, null, "OnUpdate");
        }

        private static void Apply(FurnitureHeadBar bar, string gameName, string source)
        {
            if (bar == null) return;
            IntPtr key = bar.Pointer;
            int frame = Time.frameCount;
            BarState previous;
            states.TryGetValue(key, out previous);
            try
            {
                long owner;
                int config;
                string trace;
                string display = null;
                string reason = null;
                bool haveFurniture = TryResolveOwner(bar, previous, out owner, out config, out trace);
                bool haveLabel = haveFurniture
                    && ContainerLabelStore.TryResolve(owner, config, out display, out reason);

                // 兜底：家具没认出来、或按家具查不到名字时，直接拿名称栏记录的 id
                // 当存档稳定 id 去目录里查一次。命中说明这条 id 就是存档 id。
                if (!haveLabel)
                {
                    long direct = BarInstanceId(bar);
                    if (direct != 0 && (!haveFurniture || direct != owner)
                        && ContainerLabelStore.TryResolveStable(direct, out display))
                    {
                        haveLabel = true;
                        LogOnce("stable:" + direct, "[SceneNames] " + source
                            + ": matched stable id " + direct + " (owner " + (haveFurniture ? owner.ToString() : "unknown") + ")");
                    }
                }

                if (!haveLabel)
                {
                    // 名称被清空（留空恢复）时把名称栏还原成游戏原名。
                    if (previous != null && !string.IsNullOrEmpty(previous.Display)) Restore(bar, previous);
                    // 绝大多数家具本来就没有自定义名，只记前几条未命中，避免刷日志。
                    states[key] = new BarState
                    {
                        Owner = haveFurniture ? owner : 0,
                        Config = config,
                        NextResolveFrame = frame + (haveFurniture ? 60 : 300)
                    };
                    if (missLogs < 6)
                    {
                        missLogs++;
                        LogOnce("miss:" + key, "[SceneNames] " + source
                            + (haveFurniture ? ": owner=" + owner + " config=" + config + " -> " + reason : ": " + trace));
                    }
                    return;
                }

                // 记住游戏原名，供「清空自定义名」时还原。
                string original = previous != null && !string.IsNullOrEmpty(previous.Original)
                    ? previous.Original
                    : bar._baseName;
                if (string.Equals(original, display, StringComparison.Ordinal)) original = null;

                states[key] = new BarState
                {
                    Owner = haveFurniture ? owner : 0,
                    Config = config,
                    Display = display,
                    Original = original,
                    NextResolveFrame = frame + (haveFurniture ? 16 : 60)
                };
                if (states.Count > 512) states.Clear();

                string before = bar.Text_Name == null ? null : bar.Text_Name.text;
                Write(bar, display, gameName);

                LogOnce("hit:" + key, "[SceneNames] " + source + ": owner=" + (haveFurniture ? owner.ToString() : "?")
                    + " config=" + config + " -> \"" + display + "\" (" + before + " -> "
                    + (bar.Text_Name == null ? "" : bar.Text_Name.text) + ")");
            }
            catch (Exception ex)
            {
                states[key] = new BarState { NextResolveFrame = frame + 300 };
                LogOnce("error:" + source, "[SceneNames] " + source + " failed: " + ex);
            }
        }

        // 名称栏只记着「展示用的实例 id」和一个 Transform。
        // Furniture 是纯逻辑对象（Furniture : BaseAgent），不是 Unity 组件，
        // 所以不能靠 GetComponent 拿，只能从 AgentManager 里按 id 反查。
        private static bool TryResolveOwner(FurnitureHeadBar bar, BarState cached,
            out long owner, out int config, out string trace)
        {
            owner = 0;
            config = 0;
            trace = null;

            AgentManager agents = CurrentAgents();
            if (agents == null) { trace = "agent manager unavailable"; return false; }

            // 1) 已经解析成功过：按实例 id 直接取，省掉全量扫描。
            if (cached != null && cached.Owner != 0)
            {
                Furniture known = LookupFurniture(agents, cached.Owner);
                if (known != null)
                {
                    owner = cached.Owner;
                    config = known.AgentConfigId;
                    return true;
                }
            }

            long showId = 0;
            Transform target = null;
            try
            {
                showId = bar._showInstanceId_k__BackingField;
                target = bar.Target;
            }
            catch { }

            Furniture found = null;
            if (showId != 0)
            {
                found = LookupFurniture(agents, showId);
                // InstanceId 与 ShowInstanceId 不是同一个字段，兜底扫一遍两者。
                if (found == null) found = ScanById(agents, showId);
            }
            if (found == null && target != null) found = ScanByTransform(agents, target);

            if (found == null)
            {
                // 失败时把三条线索一次打全，方便下轮直接定位，而不是反复试。
                trace = "no furniture for bar: showInstanceId=" + showId
                    + " target=" + (target == null ? "null" : target.name)
                    + " agents=" + AgentCount(agents)
                    + " samples=" + SampleAgents(agents);
                return false;
            }

            owner = found.InstanceId;
            config = found.AgentConfigId;
            if (owner == 0) { trace = "furniture has zero instance id"; return false; }
            return true;
        }

        private static AgentManager CurrentAgents()
        {
            BattleLogicWorld world = BaseSingleton<BattleLogicWorld>._Instance;
            return world == null ? null : world._AgentManager;
        }

        // 名称栏创建时记录下来的展示实例 id（可能是运行时实例 id，也可能是存档稳定 id）。
        private static long BarInstanceId(BaseHeadBar bar)
        {
            try { return bar._showInstanceId_k__BackingField; }
            catch { return 0; }
        }

        private static Furniture LookupFurniture(AgentManager agents, long id)
        {
            try
            {
                BaseAgent agent = agents.GetAgent(id);
                return agent == null ? null : agent.TryCast<Furniture>();
            }
            catch { return null; }
        }

        private static Furniture ScanById(AgentManager agents, long id)
        {
            try
            {
                var all = agents.GetAllAgent();
                if (all == null) return null;
                for (int i = 0; i < all.Count; i++)
                {
                    BaseAgent agent = all[i];
                    if (agent == null) continue;
                    if (agent.InstanceId != id && agent.ShowInstanceId != id) continue;
                    Furniture furniture = agent.TryCast<Furniture>();
                    if (furniture != null) return furniture;
                }
            }
            catch { }
            return null;
        }

        // 名称栏的 Target 可能挂在模型子节点上，所以比较整条父链。
        private static Furniture ScanByTransform(AgentManager agents, Transform target)
        {
            try
            {
                HashSet<IntPtr> chain = new HashSet<IntPtr>();
                Transform node = target;
                for (int depth = 0; depth < 12 && node != null; depth++)
                {
                    chain.Add(node.Pointer);
                    node = node.parent;
                }

                var all = agents.GetAllAgent();
                if (all == null) return null;
                for (int i = 0; i < all.Count; i++)
                {
                    BaseAgent agent = all[i];
                    if (agent == null) continue;
                    TransformComponent motion = agent._transformComponent;
                    Transform own = motion == null ? null : motion._transform_k__BackingField;
                    if (own == null || !chain.Contains(own.Pointer)) continue;
                    Furniture furniture = agent.TryCast<Furniture>();
                    if (furniture != null) return furniture;
                }
            }
            catch { }
            return null;
        }

        private static string AgentCount(AgentManager agents)
        {
            try
            {
                var all = agents.GetAllAgent();
                return all == null ? "?" : all.Count.ToString();
            }
            catch { return "?"; }
        }

        private static string SampleAgents(AgentManager agents)
        {
            try
            {
                var all = agents.GetAllAgent();
                if (all == null) return "-";
                System.Text.StringBuilder text = new System.Text.StringBuilder();
                int shown = 0;
                for (int i = 0; i < all.Count && shown < 4; i++)
                {
                    BaseAgent agent = all[i];
                    if (agent == null) continue;
                    Furniture probe = agent.TryCast<Furniture>();
                    if (probe == null) continue;
                    TransformComponent motion = agent._transformComponent;
                    Transform own = motion == null ? null : motion._transform_k__BackingField;
                    if (shown > 0) text.Append("; ");
                    text.Append(probe.AgentConfigId).Append('@').Append(probe.InstanceId)
                        .Append('/').Append(agent.ShowInstanceId)
                        .Append(" t=").Append(own == null ? "null" : own.name);
                    shown++;
                }
                return shown == 0 ? "-" : text.ToString();
            }
            catch { return "?"; }
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

        private static void Restore(FurnitureHeadBar bar, BarState state)
        {
            if (string.IsNullOrEmpty(state.Original)) return;
            Text text = bar.Text_Name;
            string current = text == null ? null : text.text;
            bar._baseName = state.Original;
            if (text != null && !string.IsNullOrEmpty(current)
                && !string.IsNullOrEmpty(state.Display)
                && current.StartsWith(state.Display, StringComparison.Ordinal))
            {
                text.text = state.Original + current.Substring(state.Display.Length);
            }
        }

        private sealed class BarState
        {
            internal long Owner;
            internal int Config;
            internal string Display;
            internal string Original;
            internal int NextResolveFrame;
        }

        private static void LogOnce(string key, string message)
        {
            if (!logged.Add(key)) return;
            if (logged.Count > 200) logged.Clear();
            ModLog.Info(message);
        }
    }
}
