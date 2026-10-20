using System;
using BepInEx.Logging;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;
using HarmonyLib;

namespace SurvivalLog.Butler
{
    // 人物增强：移动速度与探索暴露度上限按倍率放大。
    //
    // 补丁在加载时一次性挂上，倍率与开关每次都从 ButlerSettings 现读，
    // 所以游戏内设置面板改完立刻生效，不需要重开游戏，也不需要卸载补丁。
    internal static class CharacterEnhancement
    {
        internal static void Apply(Harmony harmony, ManualLogSource log)
        {
            var speedGetter = AccessTools.Method(
                typeof(AttributeComponent),
                nameof(AttributeComponent.GetTotalValue_Float),
                new[] { typeof(AttrName) });
            var exposureLimitGetter = AccessTools.PropertyGetter(typeof(ExploreManager), nameof(ExploreManager.MaxExposure));

            if (speedGetter == null)
                throw new MissingMethodException(typeof(AttributeComponent).FullName, "GetTotalValue_Float(AttrName)");
            if (exposureLimitGetter == null)
                throw new MissingMethodException(typeof(ExploreManager).FullName, "get_MaxExposure");

            harmony.Patch(speedGetter, postfix: new HarmonyMethod(typeof(CharacterEnhancement), nameof(ScaleMoveSpeed)));
            harmony.Patch(exposureLimitGetter, postfix: new HarmonyMethod(typeof(CharacterEnhancement), nameof(ScaleExposureLimit)));

            if (log != null)
                log.LogInfo("Character enhancement active: move speed x" + ButlerSettings.MoveSpeedMultiplier
                    + "; exploration exposure limit x" + ButlerSettings.ExposureMultiplier + ".");
        }

        private static void ScaleMoveSpeed(AttrName attrName, ref float __result)
        {
            if (attrName != AttrName.MoveSpeed) return;
            if (!ButlerSettings.EnhanceEnabled) return;
            __result *= ButlerSettings.MoveSpeedMultiplier;
        }

        private static void ScaleExposureLimit(ref int __result)
        {
            if (!ButlerSettings.EnhanceEnabled) return;
            // 倍率是浮点数、目标字段是 int：先按浮点算再四舍五入，
            // 避免 (int) 直接截断让 1.5 倍这类倍率每级都少一点。
            double scaled = Math.Round(__result * (double)ButlerSettings.ExposureMultiplier, MidpointRounding.AwayFromZero);
            if (scaled > int.MaxValue) scaled = int.MaxValue;
            __result = (int)scaled;
        }
    }
}
