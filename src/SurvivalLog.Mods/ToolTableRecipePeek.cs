using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using GameCore.HotUpdate.ReduxUI;
using HarmonyLib;
using UnityEngine;

namespace SurvivalLog.Butler
{
    // 制作台「右键只看不造」。
    //
    // 为什么需要这一层：HomeButler 的 Compatibility（NativeFix.Tick）在帮玩家把材料摆上台面之后，
    // 会自己再发一次 Ac_ToolTable_Make.SendAction 直接开工（日志里的
    // "[NativeCompat] Craft sent through native action."）。工作台一旦开工，页面就会进入
    // lastCraftingState，预览区的染色色块点不动，玩家没有选色的机会。
    //
    // 所以右键要做的是：照常让配方上台面，但把紧随其后的那一次自动开工拦掉。
    internal static class ToolTableRecipePeek
    {
        internal const string PeekMessage = "SL_RECIPE_PEEK";

        private static readonly HashSet<string> SeenTypes = new HashSet<string>();

        private static bool armed;
        private static float armDeadline;
        private static int budget;

        internal static void Arm(string via)
        {
            armed = true;
            budget = 1;
            armDeadline = Time.realtimeSinceStartup + 10f;
            ModLog.Info("[RecipePeek] right-click armed via " + via + ".");
        }

        // 页面在 RECIPE_CLICK 的 json 里塞了 slPeek 时的第二条通路。
        internal static void NoteRecipeClick(string json)
        {
            if (armed) return;
            if (json != null && json.IndexOf("slPeek", StringComparison.OrdinalIgnoreCase) >= 0)
                Arm("recipe payload");
        }

        internal static bool ShouldSuppress(string json)
        {
            if (!armed) return false;
            if (Time.realtimeSinceStartup > armDeadline)
            {
                armed = false;
                return false;
            }
            if (budget <= 0) return false;
            budget--;
            armed = false;
            ModLog.Info("[RecipePeek] suppressed the automatic craft: " + Shorten(json));
            return true;
        }

        internal static void NoteDye(string json)
        {
            ModLog.Info("[RecipePeek] DYE_PICK " + Shorten(json));
        }

        internal static void NoteMessage(string page, string type, string json)
        {
            if (!string.Equals(page, "ToolTable", StringComparison.Ordinal)) return;
            if (string.Equals(type, PeekMessage, StringComparison.Ordinal))
            {
                Arm("page message");
                return;
            }
            if (SeenTypes.Count < 40 && SeenTypes.Add(type ?? "?"))
                ModLog.Info("[RecipePeek] page message: " + type);
        }

        internal static void Install(Harmony harmony)
        {
            try
            {
                MethodBase target = AccessTools.Method(typeof(WebUILayer), "OnPageMessage");
                if (target == null)
                {
                    ModLog.Warning("[RecipePeek] WebUILayer.OnPageMessage not found.");
                    return;
                }
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(RecipePeekPageMessagePatch), nameof(RecipePeekPageMessagePatch.Prefix)));
                ModLog.Info("[RecipePeek] Hooked WebUILayer.OnPageMessage.");
            }
            catch (Exception ex)
            {
                ModLog.Warning("[RecipePeek] page message hook failed: " + (ex.InnerException ?? ex).Message);
            }
        }

        private static string Shorten(string json)
        {
            if (string.IsNullOrEmpty(json)) return "(empty)";
            return json.Length <= 160 ? json : json.Substring(0, 160) + "...";
        }
    }

    // 网页消息的唯一收口（协议 3 全部走这里），右键标记从这里进。
    internal static class RecipePeekPageMessagePatch
    {
        internal static bool Prefix(string __0, string __1, string __2)
        {
            try
            {
                if (string.Equals(__1, ToolTableRecipePeek.PeekMessage, StringComparison.Ordinal))
                    return false; // 自己吃掉，不往游戏里派发
                ToolTableRecipePeek.NoteMessage(__0, __1, __2);
            }
            catch { }
            return true;
        }
    }

    // 兜底：万一 OnPageMessage 这台机器的签名对不上，HandleCallback 这条老路也留着。
    [HarmonyPatch(typeof(WebUIVm), "HandleCallback")]
    internal static class RecipePeekHandleCallbackPatch
    {
        private static bool Prefix(WebUIVm __instance, string messageType, string jsonData)
        {
            try
            {
                if (string.Equals(messageType, ToolTableRecipePeek.PeekMessage, StringComparison.Ordinal))
                {
                    ToolTableRecipePeek.Arm("HandleCallback");
                    return false;
                }
            }
            catch { }
            return true;
        }
    }

    // Priority.First 是必需的：HomeButler 的 DeferredRecipe 也是这个方法的前置补丁，
    // 它拿到一次配方点击后会 return false（把请求排队、跳过原方法），
    // 排在它后面的前置补丁就不会再被执行。
    [HarmonyPatch(typeof(Ac_ToolTable_RecipeClick), "SendAction")]
    [HarmonyPriority(Priority.First)]
    internal static class RecipePeekRecipeClickPatch
    {
        private static void Prefix(string __0)
        {
            try { ToolTableRecipePeek.NoteRecipeClick(__0); } catch { }
        }
    }

    [HarmonyPatch(typeof(Ac_ToolTable_Make), "SendAction")]
    internal static class RecipePeekMakePatch
    {
        private static bool Prefix(string __0)
        {
            try { if (ToolTableRecipePeek.ShouldSuppress(__0)) return false; }
            catch { }
            return true;
        }
    }

    [HarmonyPatch(typeof(Ac_ToolTable_DyePick), "SendAction")]
    internal static class RecipePeekDyePickPatch
    {
        private static void Prefix(string __0)
        {
            try { ToolTableRecipePeek.NoteDye(__0); } catch { }
        }
    }
}
