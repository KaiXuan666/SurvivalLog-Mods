using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;
using GameCore.HotUpdate.ReduxUI;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace SurvivalLog.Butler
{
    // 全容器官方改名支持：
    // 官方更新原生增加了储物柜改名机制（基于 FurnitureTagComponent 与 PushHeadBarCustomName），
    // 但原生限定了必须是储物柜且在 HomeMap 下才生效。
    // 本模块为所有容器家具补全 FurnitureTagComponent 并突破白名单限制，
    // 完全复用官方改名、官方持久化存档和场景头顶栏原生刷新。
    // 同时支持玩家个人背包（ATab0）改名，
    // 并在打开界面、切换页签与刷新时，彻底同步 BackpackName_B、BackpackName_A 与 CustomNameMap，
    // 确保再次打开容器时永远显示自定义名称。
    public static class ContainerRenameNative
    {
        public static bool IsEditingName { get; set; }

        private static string _customBackpackName;
        private static readonly string BackpackNameFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "BepInEx", "config", "BackpackCustomName.txt");

        static ContainerRenameNative()
        {
            try
            {
                if (File.Exists(BackpackNameFilePath))
                {
                    _customBackpackName = File.ReadAllText(BackpackNameFilePath).Trim();
                }
            }
            catch { }
        }

        public static string CustomBackpackName
        {
            get => _customBackpackName;
            set
            {
                _customBackpackName = value;
                try
                {
                    string dir = Path.GetDirectoryName(BackpackNameFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.WriteAllText(BackpackNameFilePath, value ?? string.Empty);
                }
                catch { }
            }
        }

        public static FurnitureTagComponent EnsureTagComponent(Furniture furniture)
        {
            if (furniture == null) return null;
            try
            {
                var comp = AgentTools.GetAgentComponent<FurnitureTagComponent>(furniture);
                if (comp == null)
                {
                    comp = AgentTools.AddAgentComponent<FurnitureTagComponent>(furniture);
                }
                return comp;
            }
            catch (Exception ex)
            {
                ModLog.Warning("[ContainerRename] EnsureTagComponent failed: " + ex.Message);
                return null;
            }
        }

        public static Furniture FindFurnitureByOwner(long ownerId)
        {
            if (ownerId == 0) return null;
            try
            {
                BattleLogicWorld world = BaseSingleton<BattleLogicWorld>._Instance;
                AgentManager agents = world == null ? null : world._AgentManager;
                if (agents == null) return null;

                var bagFurnitures = agents.GetAllBagFurnitures();
                if (bagFurnitures != null)
                {
                    foreach (var bag in bagFurnitures)
                    {
                        if (bag != null && bag.InstanceId == ownerId)
                        {
                            return bag.TryCast<Furniture>();
                        }
                    }
                }

                var allFurnitures = agents.GetAllFurnitures();
                if (allFurnitures != null)
                {
                    foreach (var furn in allFurnitures)
                    {
                        if (furn != null && furn.InstanceId == ownerId)
                        {
                            return furn;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning("[ContainerRename] FindFurnitureByOwner failed: " + ex.Message);
            }
            return null;
        }

        // 核心：无论打开、切换、刷新，均强制将自定义名称赋给 BackpackName_B / BackpackName_A / CustomNameMap
        public static void SyncStateCustomNames(State_Web_BackpackUI state)
        {
            if (state == null) return;
            try
            {
                // 1. 同步右侧 B 面板打开的容器
                if (state.OwnerId_B != 0)
                {
                    Furniture furnB = FindFurnitureByOwner(state.OwnerId_B);
                    if (furnB != null)
                    {
                        string customB = furnB.GetCustomNameNow();
                        if (!string.IsNullOrEmpty(customB))
                        {
                            if (state.BackpackName_B != null)
                                state.BackpackName_B.Value = customB;
                            if (state.CustomName != null)
                                state.CustomName.Value = customB;
                        }
                    }
                }

                // 2. 同步左侧 A 面板当前容器
                if (state.CurATab == 0)
                {
                    if (!string.IsNullOrEmpty(CustomBackpackName))
                    {
                        state.ATab0CustomName = CustomBackpackName;
                        if (state.BackpackName_A != null)
                            state.BackpackName_A.Value = CustomBackpackName;
                    }
                }
                else
                {
                    long ownerA = state.OwnerId_A;
                    if (ownerA == 0 && state.ATabOwnerIds != null && state.CurATab < state.ATabOwnerIds.Count)
                    {
                        ownerA = state.ATabOwnerIds[state.CurATab];
                    }
                    if (ownerA != 0)
                    {
                        Furniture furnA = FindFurnitureByOwner(ownerA);
                        if (furnA != null)
                        {
                            string customA = furnA.GetCustomNameNow();
                            if (!string.IsNullOrEmpty(customA) && state.BackpackName_A != null)
                            {
                                state.BackpackName_A.Value = customA;
                            }
                        }
                    }
                }

                // 3. 同步到 State_Data_Furniture 的 CustomNameMap
                try
                {
                    ReduxUISystem redux = BaseSingleton<ReduxUISystem>._Instance;
                    if (redux != null && redux.reduxStoreLayer != null)
                    {
                        State_Data_Furniture furnData = redux.reduxStoreLayer.GetState<State_Data_Furniture>(Il2CppType.Of<State_Data_Furniture>());
                        if (furnData != null && furnData.CustomNameMap != null)
                        {
                            if (state.OwnerId_B != 0)
                            {
                                Furniture furnB = FindFurnitureByOwner(state.OwnerId_B);
                                string customB = furnB != null ? furnB.GetCustomNameNow() : null;
                                if (!string.IsNullOrEmpty(customB))
                                {
                                    furnData.CustomNameMap[state.OwnerId_B] = customB;
                                }
                            }
                        }
                    }
                }
                catch { }

                // 4. 彻底同步左侧页签 JSON（ATabsJson.Value）
                UpdateATabsJsonWithCustomNames(state);
            }
            catch (Exception ex)
            {
                ModLog.Warning("[ContainerRename] SyncStateCustomNames failed: " + ex.Message);
            }
        }

        // 遍历并更新 ATabsJson 里的每一项 tab 的 name 为真实自定义名称
        public static void UpdateATabsJsonWithCustomNames(State_Web_BackpackUI state)
        {
            if (state == null || state.ATabsJson == null) return;
            string json = state.ATabsJson.Value;
            if (string.IsNullOrEmpty(json)) return;

            try
            {
                var array = JsonNode.Parse(json)?.AsArray();
                if (array == null) return;

                bool changed = false;
                for (int i = 0; i < array.Count; i++)
                {
                    var node = array[i];
                    if (node == null) continue;

                    string ownerStr = node["hbOwner"]?.GetValue<string>();
                    long ownerId = 0;
                    if (string.IsNullOrEmpty(ownerStr) && state.ATabOwnerIds != null && i < state.ATabOwnerIds.Count)
                    {
                        ownerId = state.ATabOwnerIds[i];
                        ownerStr = ownerId.ToString();
                        node["hbOwner"] = ownerStr;
                        changed = true;
                    }
                    else if (!string.IsNullOrEmpty(ownerStr))
                    {
                        long.TryParse(ownerStr, out ownerId);
                    }

                    if (i == 0 || ownerId == 0)
                    {
                        if (!string.IsNullOrEmpty(CustomBackpackName))
                        {
                            string oldName = node["name"]?.GetValue<string>();
                            if (oldName != CustomBackpackName)
                            {
                                node["name"] = CustomBackpackName;
                                changed = true;
                            }
                        }
                    }
                    else if (ownerId != 0)
                    {
                        Furniture furn = FindFurnitureByOwner(ownerId);
                        string cName = furn != null ? furn.GetCustomNameNow() : null;
                        if (!string.IsNullOrEmpty(cName))
                        {
                            string oldName = node["name"]?.GetValue<string>();
                            if (oldName != cName)
                            {
                                node["name"] = cName;
                                changed = true;
                            }
                        }
                    }
                }

                if (changed)
                {
                    state.ATabsJson.Value = array.ToJsonString();
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning("[ContainerRename] UpdateATabsJsonWithCustomNames failed: " + ex.Message);
            }
        }

        // 收集所有有效自定义名称字典，包含背包与所有家具
        public static Dictionary<string, string> GetAllCustomNames(State_Web_BackpackUI state = null)
        {
            var dict = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(CustomBackpackName))
            {
                dict["backpack"] = CustomBackpackName;
                dict["0"] = CustomBackpackName;
            }

            try
            {
                BattleLogicWorld world = BaseSingleton<BattleLogicWorld>._Instance;
                AgentManager agents = world == null ? null : world._AgentManager;
                if (agents != null)
                {
                    var bagFurnitures = agents.GetAllBagFurnitures();
                    if (bagFurnitures != null)
                    {
                        foreach (var bag in bagFurnitures)
                        {
                            if (bag == null) continue;
                            var furn = bag.TryCast<Furniture>();
                            if (furn == null) continue;
                            string name = furn.GetCustomNameNow();
                            if (!string.IsNullOrEmpty(name))
                            {
                                dict[furn.InstanceId.ToString()] = name;
                            }
                        }
                    }

                    var allFurnitures = agents.GetAllFurnitures();
                    if (allFurnitures != null)
                    {
                        foreach (var furn in allFurnitures)
                        {
                            if (furn == null) continue;
                            string name = furn.GetCustomNameNow();
                            if (!string.IsNullOrEmpty(name))
                            {
                                dict[furn.InstanceId.ToString()] = name;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning("[ContainerRename] GetAllCustomNames error: " + ex.Message);
            }

            return dict;
        }

        // 向 WebUI 广播全量自定义名称字典
        public static void BroadcastAllCustomNames(WebUILayer layer, State_Web_BackpackUI state = null)
        {
            if (layer == null) return;
            try
            {
                var dict = GetAllCustomNames(state);
                string json = JsonSerializer.Serialize(dict);
                layer.BroadcastToAllPages("HB_CONTAINER_NAMES_SYNC", json);
            }
            catch (Exception ex)
            {
                ModLog.Warning("[ContainerRename] BroadcastAllCustomNames error: " + ex.Message);
            }
        }

        // 控制 WebUI 键盘与输入法使能
        public static void SetFocusState(WebUILayer layer, bool focused)
        {
            IsEditingName = focused;

            try
            {
                if (layer != null)
                {
                    layer.SetKeyboardEnabled(focused);
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning("[ContainerRename] SetKeyboardEnabled error: " + ex.Message);
            }

            try
            {
                ReduxUISystem redux = BaseSingleton<ReduxUISystem>._Instance;
                State_Web_BackpackUI ui = redux == null || redux.reduxStoreLayer == null
                    ? null
                    : redux.reduxStoreLayer.GetState<State_Web_BackpackUI>(Il2CppType.Of<State_Web_BackpackUI>());
                if (ui != null)
                {
                    ui.IsNameEditing = focused;
                }
            }
            catch { }
        }

        public static void HandleRenameRequest(WebUILayer layer, string requestJson)
        {
            if (string.IsNullOrEmpty(requestJson)) return;
            try
            {
                using var doc = JsonDocument.Parse(requestJson);
                var root = doc.RootElement;
                string ownerStr = root.TryGetProperty("ownerId", out var op) ? op.GetString() : null;
                string newName = root.TryGetProperty("name", out var np) ? np.GetString() : null;
                bool isBackpack = string.Equals(ownerStr, "backpack", StringComparison.OrdinalIgnoreCase) ||
                                  ownerStr == "0";

                string sanitized = FurnitureCustomNameRules.Sanitize(newName);

                ReduxUISystem redux = BaseSingleton<ReduxUISystem>._Instance;
                State_Web_BackpackUI ui = redux == null || redux.reduxStoreLayer == null
                    ? null
                    : redux.reduxStoreLayer.GetState<State_Web_BackpackUI>(Il2CppType.Of<State_Web_BackpackUI>());

                if (isBackpack)
                {
                    CustomBackpackName = sanitized;
                    if (ui != null)
                    {
                        ui.ATab0CustomName = sanitized;
                        if (ui.CurATab == 0 && ui.BackpackName_A != null)
                        {
                            ui.BackpackName_A.Value = sanitized;
                        }
                        UpdateATabsJsonWithCustomNames(ui);
                    }
                    ModLog.Info($"[ContainerRename] Set custom backpack name: '{sanitized}'");

                    if (layer != null)
                    {
                        var responseObj = new { ownerId = "backpack", name = sanitized };
                        layer.BroadcastToAllPages("HB_RENAME_CONTAINER_RESULT", JsonSerializer.Serialize(responseObj));
                        BroadcastAllCustomNames(layer, ui);
                    }
                    return;
                }

                if (!long.TryParse(ownerStr, out long ownerId) || ownerId == 0)
                {
                    if (ui != null && ui.OwnerId_B != 0)
                    {
                        ownerId = ui.OwnerId_B;
                    }
                }

                if (ownerId == 0)
                {
                    ModLog.Warning("[ContainerRename] Cannot resolve target container owner ID.");
                    return;
                }

                Furniture target = FindFurnitureByOwner(ownerId);
                if (target != null)
                {
                    var tag = EnsureTagComponent(target);
                    target.OnSetFurnitureCustomName(sanitized);
                    ModLog.Info($"[ContainerRename] Set custom name '{sanitized}' for furniture {ownerId}.");
                }
                else
                {
                    ModLog.Warning($"[ContainerRename] Furniture {ownerId} not found in AgentManager.");
                }

                // 强制同步 ReduxUI 各状态与页签 JSON
                if (ui != null)
                {
                    SyncStateCustomNames(ui);
                    UpdateATabsJsonWithCustomNames(ui);
                }

                // 广播结果给 WebUI
                if (layer != null)
                {
                    var responseObj = new { ownerId = ownerId.ToString(), name = sanitized };
                    string respJson = JsonSerializer.Serialize(responseObj);
                    layer.BroadcastToAllPages("HB_RENAME_CONTAINER_RESULT", respJson);
                    BroadcastAllCustomNames(layer, ui);
                }
            }
            catch (Exception ex)
            {
                ModLog.Error("[ContainerRename] HandleRenameRequest failed: " + ex);
            }
            finally
            {
                SetFocusState(layer, false);
            }
        }
    }

    // Vuplex 转发 IME 组合输入；Unity 必须先放行 OS 输入法
    [HarmonyPatch(typeof(WebUILayer), nameof(WebUILayer.SetKeyboardEnabled))]
    internal static class InlineIme
    {
        private static bool changed;
        private static IMECompositionMode previous;
        private static bool auto;

        private static void Postfix(bool __0)
        {
            if (__0)
            {
                if (!changed)
                {
                    previous = Input.imeCompositionMode;
                    auto = Vuplex.WebView.Web.AutoEnableIme;
                    changed = true;
                }
                Vuplex.WebView.Web.AutoEnableIme = true;
                Input.imeCompositionMode = IMECompositionMode.On;
            }
            else if (changed)
            {
                Input.imeCompositionMode = previous;
                Vuplex.WebView.Web.AutoEnableIme = auto;
                changed = false;
            }
        }
    }

    // 突破官方白名单：任何家具改名时均保存到 FurnitureTagComponent 并刷新头顶栏
    [HarmonyPatch(typeof(Furniture), nameof(Furniture.OnSetFurnitureCustomName))]
    internal static class Furniture_OnSetFurnitureCustomName_Patch
    {
        private static bool Prefix(Furniture __instance, string name)
        {
            if (__instance == null) return false;
            var tagComp = ContainerRenameNative.EnsureTagComponent(__instance);
            if (tagComp != null)
            {
                tagComp.SetCustomName(name);
                __instance.PushCustomNameShow(tagComp);
            }
            __instance.PushHeadBarCustomName();
            return false; // 跳过原版白名单限制
        }
    }

    // 突破官方白名单：读取自定义名字时只要有值即返回
    [HarmonyPatch(typeof(Furniture), nameof(Furniture.GetCustomNameNow), new Type[] { })]
    internal static class Furniture_GetCustomNameNow_Patch
    {
        private static bool Prefix(Furniture __instance, ref string __result)
        {
            if (__instance == null) return true;
            var tagComp = AgentTools.GetAgentComponent<FurnitureTagComponent>(__instance);
            if (tagComp != null && !string.IsNullOrEmpty(tagComp.CustomName))
            {
                __result = tagComp.CustomName;
                return false;
            }
            return true;
        }
    }

    // 突破官方白名单：对所有有效容器放行 CanTagNow
    [HarmonyPatch(typeof(Furniture), nameof(Furniture.CanTagNow))]
    internal static class Furniture_CanTagNow_Patch
    {
        private static bool Prefix(Furniture __instance, ref bool __result)
        {
            if (__instance != null)
            {
                __result = true;
                return false;
            }
            return true;
        }
    }

    // 放行 Reducer_Web_BackpackUI.RA_SetCustomName 的改名请求
    [HarmonyPatch(typeof(Reducer_Web_BackpackUI), nameof(Reducer_Web_BackpackUI.RA_SetCustomName))]
    internal static class Reducer_Web_BackpackUI_RA_SetCustomName_Patch
    {
        private static bool Prefix(Ac_BackpackUI_SetCustomName ac, State_Web_BackpackUI state, ref State_Web_BackpackUI __result)
        {
            if (state == null) return true;
            string rawName = ac != null ? ac.Name : null;
            string sanitized = FurnitureCustomNameRules.Sanitize(rawName);

            Furniture target = ContainerRenameNative.FindFurnitureByOwner(state.OwnerId_B);
            if (target != null)
            {
                target.OnSetFurnitureCustomName(sanitized);
            }
            if (state.CustomName != null)
            {
                state.CustomName.Value = sanitized;
            }
            if (state.BackpackName_B != null)
            {
                state.BackpackName_B.Value = sanitized;
            }
            __result = state;
            return false;
        }
    }

    // 打开背包面板时，强制同步自定义名称并向页面广播
    [HarmonyPatch(typeof(Reducer_Web_BackpackUI), nameof(Reducer_Web_BackpackUI.RA_OpenUI))]
    internal static class Reducer_Web_BackpackUI_RA_OpenUI_Patch
    {
        private static void Postfix(State_Web_BackpackUI __result)
        {
            ContainerRenameNative.SyncStateCustomNames(__result);
            ContainerRenameNative.UpdateATabsJsonWithCustomNames(__result);
            ReduxUISystem redux = BaseSingleton<ReduxUISystem>._Instance;
            WebUILayer layer = redux == null ? null : redux.GetWebUILayer();
            ContainerRenameNative.BroadcastAllCustomNames(layer, __result);
        }
    }

    // 刷新背包时，强制同步自定义名称
    [HarmonyPatch(typeof(Reducer_Web_BackpackUI), nameof(Reducer_Web_BackpackUI.RA_RefreshBag))]
    internal static class Reducer_Web_BackpackUI_RA_RefreshBag_Patch
    {
        private static void Postfix(State_Web_BackpackUI __result)
        {
            ContainerRenameNative.SyncStateCustomNames(__result);
            ContainerRenameNative.UpdateATabsJsonWithCustomNames(__result);
        }
    }

    // 切换页签时，强制同步自定义名称
    [HarmonyPatch(typeof(Reducer_Web_BackpackUI), nameof(Reducer_Web_BackpackUI.RA_SwitchTab))]
    internal static class Reducer_Web_BackpackUI_RA_SwitchTab_Patch
    {
        private static void Postfix(State_Web_BackpackUI __result)
        {
            ContainerRenameNative.SyncStateCustomNames(__result);
            ContainerRenameNative.UpdateATabsJsonWithCustomNames(__result);
        }
    }

    // 彻底解决按键穿透：改名编辑期间阻断 A/D 等快捷键切换页签
    [HarmonyPatch(typeof(Reducer_Web_BackpackUI), nameof(Reducer_Web_BackpackUI.RA_SwitchTabByKey))]
    internal static class Reducer_Web_BackpackUI_RA_SwitchTabByKey_Patch
    {
        private static bool Prefix(Ac_BackpackUI_SwitchTabByKey ac, State_Web_BackpackUI state, ref State_Web_BackpackUI __result)
        {
            if (ContainerRenameNative.IsEditingName || (state != null && state.IsNameEditing))
            {
                __result = state;
                return false;
            }
            return true;
        }
    }

    // 追踪官方的 NameEditFocus 状态
    [HarmonyPatch(typeof(Reducer_Web_BackpackUI), nameof(Reducer_Web_BackpackUI.RA_NameEditFocus))]
    internal static class Reducer_Web_BackpackUI_RA_NameEditFocus_Patch
    {
        private static void Postfix(Ac_BackpackUI_NameEditFocus ac, State_Web_BackpackUI state)
        {
            if (ac != null)
            {
                ContainerRenameNative.IsEditingName = ac.Focused;
                ReduxUISystem redux = BaseSingleton<ReduxUISystem>._Instance;
                WebUILayer layer = redux == null ? null : redux.GetWebUILayer();
                if (layer != null)
                {
                    layer.SetKeyboardEnabled(ac.Focused);
                }
            }
        }
    }

    // 在 HomeButler 的 BackpackTabs.Postfix 运行之后，最后执行本补丁，
    // 将 ATabsJson.Value 中的每一项 tab 的 name 彻底替换为自定义名称
    [HarmonyPatch(typeof(Reducer_Web_BackpackUI), nameof(Reducer_Web_BackpackUI.BuildATabs))]
    internal static class Reducer_Web_BackpackUI_BuildATabs_Patch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(State_Web_BackpackUI state)
        {
            ContainerRenameNative.UpdateATabsJsonWithCustomNames(state);
        }
    }

    // 确保背包生成页签时，应用自定义背包名称和各容器名称
    [HarmonyPatch(typeof(Reducer_Web_BackpackUI), nameof(Reducer_Web_BackpackUI.BuildATabsJson))]
    internal static class Reducer_Web_BackpackUI_BuildATabsJson_Patch
    {
        private static void Prefix(State_Web_BackpackUI state)
        {
            if (state == null) return;
            if (!string.IsNullOrEmpty(ContainerRenameNative.CustomBackpackName))
            {
                state.ATab0CustomName = ContainerRenameNative.CustomBackpackName;
            }

            try
            {
                ReduxUISystem redux = BaseSingleton<ReduxUISystem>._Instance;
                if (redux != null && redux.reduxStoreLayer != null)
                {
                    State_Data_Furniture furnData = redux.reduxStoreLayer.GetState<State_Data_Furniture>(Il2CppType.Of<State_Data_Furniture>());
                    if (furnData != null && furnData.CustomNameMap != null && state.ATabOwnerIds != null)
                    {
                        foreach (var owner in state.ATabOwnerIds)
                        {
                            Furniture furn = ContainerRenameNative.FindFurnitureByOwner(owner);
                            if (furn != null)
                            {
                                string cName = furn.GetCustomNameNow();
                                if (!string.IsNullOrEmpty(cName))
                                {
                                    furnData.CustomNameMap[owner] = cName;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }
    }

    // WebUIVm 接收前端改名和输入焦点请求
    [HarmonyPatch(typeof(WebUIVm), "HandleCallback")]
    internal static class ContainerRenameMessagePatch
    {
        private static bool Prefix(WebUIVm __instance, string messageType, string jsonData)
        {
            if (!string.Equals(__instance.UIKey, "Backpack", StringComparison.Ordinal)) return true;

            if (string.Equals(messageType, "HB_REQUEST_CONTAINER_NAMES", StringComparison.Ordinal))
            {
                ReduxUISystem redux = BaseSingleton<ReduxUISystem>._Instance;
                WebUILayer layer = redux == null ? null : redux.GetWebUILayer();
                State_Web_BackpackUI ui = redux == null || redux.reduxStoreLayer == null
                    ? null
                    : redux.reduxStoreLayer.GetState<State_Web_BackpackUI>(Il2CppType.Of<State_Web_BackpackUI>());
                ContainerRenameNative.BroadcastAllCustomNames(layer, ui);
                return false;
            }

            if (string.Equals(messageType, "HB_NAME_EDIT_FOCUS", StringComparison.Ordinal))
            {
                try
                {
                    using var doc = JsonDocument.Parse(jsonData);
                    bool focused = doc.RootElement.TryGetProperty("focused", out var fp) && fp.GetBoolean();
                    ReduxUISystem redux = BaseSingleton<ReduxUISystem>._Instance;
                    WebUILayer layer = redux == null ? null : redux.GetWebUILayer();
                    ContainerRenameNative.SetFocusState(layer, focused);
                }
                catch { }
                return false;
            }

            if (string.Equals(messageType, "HB_RENAME_CONTAINER", StringComparison.Ordinal))
            {
                ReduxUISystem redux = BaseSingleton<ReduxUISystem>._Instance;
                WebUILayer layer = redux == null ? null : redux.GetWebUILayer();
                ContainerRenameNative.HandleRenameRequest(layer, jsonData);
                return false;
            }

            return true;
        }
    }
}
