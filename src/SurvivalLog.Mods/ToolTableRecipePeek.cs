using System;
using BepInEx.Logging;
using GameCore.HotUpdate.ReduxUI;
using HarmonyLib;
using SurvivalLog.Mods;
using UnityEngine;

namespace SurvivalLog.Mods
{
    // 制作台"右键只看不造"：网页脚本在右键时会先发一条 SL_RECIPE_PEEK，
    // 再补一次原生点击（RECIPE_CLICK）。原生配方选择之后如果紧接着自己就把东西造了，
    // 玩家就没有机会在预览区选染色，所以这里把「右键那一次选择所引起的制作」拦掉；
    // 之后玩家自己点「制造」不受影响。
    internal static class ToolTableRecipePeek
    {
        private static float peekDeadline;
        private static bool peekForCurrentClick;
        private static int clickDepth;
        private static int lastClickFrame = -1000;
        private static int peekExitFrame = -1000;
        private static bool loggedFirstCraftAfterClick;

        internal static void RequestPeek(string uiKey)
        {
            peekDeadline = Time.realtimeSinceStartup + 3f;
            ModLog.Info("[RecipePeek] Right-click peek requested from page " + (uiKey ?? "?") + ".");
        }

        internal static void OnRecipeClickEnter(string json)
        {
            clickDepth++;
            peekForCurrentClick = Time.realtimeSinceStartup <= peekDeadline;
            if (peekForCurrentClick) peekDeadline = 0f;
            if (peekForCurrentClick)
                ModLog.Info("[RecipePeek] RECIPE_CLICK (right-click): " + Shorten(json));
        }

        internal static void OnRecipeClickExit()
        {
            if (clickDepth > 0) clickDepth--;
            lastClickFrame = Time.frameCount;
            if (peekForCurrentClick)
            {
                peekForCurrentClick = false;
                peekExitFrame = lastClickFrame;
                ModLog.Info("[RecipePeek] Recipe loaded without crafting.");
            }
        }

        // 返回 true 表示这次制作要拦掉。
        internal static bool ShouldSuppressCraft(string json)
        {
            // 只拦「右键那一下选择配方所引起的制作」：要么发生在选择调用内部，
            // 要么就在同一帧/下一帧由它异步触发。人手不可能在 2 帧内再点一次「制造」。
            bool insideClick = clickDepth > 0 && peekForCurrentClick;
            bool rightAfterPeek = Time.frameCount - peekExitFrame <= 2;
            if (insideClick || rightAfterPeek)
            {
                ModLog.Info("[RecipePeek] Suppressed CRAFT triggered by the recipe click: " + Shorten(json));
                if (!insideClick) peekExitFrame = -1000;
                return true;
            }

            bool rightAfterAnyClick = Time.frameCount - lastClickFrame <= 2;
            if (rightAfterAnyClick && !loggedFirstCraftAfterClick)
            {
                // 只记一次，用来判断原版配方选择是否自己会接着制作。
                loggedFirstCraftAfterClick = true;
                ModLog.Info("[RecipePeek] CRAFT ran right after a recipe click: " + Shorten(json));
            }
            return false;
        }

        internal static void OnDyePick(string json)
        {
            ModLog.Info("[RecipePeek] DYE_PICK " + Shorten(json));
        }

        private static string Shorten(string json)
        {
            if (string.IsNullOrEmpty(json)) return "(empty)";
            return json.Length <= 160 ? json : json.Substring(0, 160) + "...";
        }
    }
}

[HarmonyPatch(typeof(WebUIVm), "HandleCallback")]
internal static class RecipePeekMessagePatch
{
    private static bool Prefix(WebUIVm __instance, string messageType, string jsonData)
    {
        if (!string.Equals(messageType, "SL_RECIPE_PEEK", StringComparison.Ordinal)) return true;
        ToolTableRecipePeek.RequestPeek(__instance == null ? null : __instance.UIKey);
        return false;
    }
}

[HarmonyPatch(typeof(Ac_ToolTable_RecipeClick), "SendAction")]
internal static class RecipePeekRecipeClickPatch
{
    private static void Prefix(string __0)
    {
        try { ToolTableRecipePeek.OnRecipeClickEnter(__0); } catch { }
    }

    private static void Postfix()
    {
        try { ToolTableRecipePeek.OnRecipeClickExit(); } catch { }
    }
}

[HarmonyPatch(typeof(Ac_ToolTable_Make), "SendAction")]
internal static class RecipePeekMakePatch
{
    private static bool Prefix(string __0)
    {
        try { if (ToolTableRecipePeek.ShouldSuppressCraft(__0)) return false; }
        catch { }
        return true;
    }
}

[HarmonyPatch(typeof(Ac_ToolTable_DyePick), "SendAction")]
internal static class RecipePeekDyePickPatch
{
    private static void Prefix(string __0)
    {
        try { ToolTableRecipePeek.OnDyePick(__0); } catch { }
    }
}
