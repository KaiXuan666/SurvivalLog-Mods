using System;
using GameCore.HotUpdate;
using GameCore.HotUpdate.ReduxUI;
using HarmonyLib;

namespace SurvivalLog.Butler
{
    // 把主界面模组面板发来的消息接进 Unity。
    //
    // 必须在 WebUIVm.HandleCallback 上拦，而不是 WebUILayer.OnPageMessage：
    // 页面经 Root.html 转发过来的消息最终是在 HandleCallback 分发的，
    // 只挂 OnPageMessage 会表现为「按钮点了没反应」。
    [HarmonyPatch(typeof(WebUIVm), "HandleCallback")]
    internal static class ButlerMenuMessagePatch
    {
        private static bool Prefix(WebUIVm __instance, string messageType, string jsonData)
        {
            if (messageType == null) return true;
            if (messageType != "SL_BUTLER_QUERY" && messageType != "SL_BUTLER_APPLY") return true;

            // 用类型判断而不是 UIKey 字符串比较：UIKey 是运行时调用得到的虚属性，
            // 具体取值依赖游戏侧实现，而「消息来自主界面」这一点用类型最可靠。
            // 其它页面若也发同名消息，直接放行给游戏自己处理，不在这里吞掉。
            if (!(__instance is WebUI_MainUI)) return true;

            try
            {
                ReduxUISystem redux = BaseSingleton<ReduxUISystem>._Instance;
                WebUILayer layer = redux == null ? null : redux.GetWebUILayer();

                ModLog.Info("[ButlerMenu] Received " + messageType + ".");
                ButlerMenuBridge.Handle(layer, messageType, jsonData);
            }
            catch (Exception ex)
            {
                // 设置面板出错不能把主界面按钮的回调链路一起带走，这里兜住并留日志。
                ModLog.Error("[ButlerMenu] Failed to handle " + messageType + ".", ex);
            }
            return false;
        }
    }
}
