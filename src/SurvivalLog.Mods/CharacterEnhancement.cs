using System;
using System.Reflection;
using BepInEx.Logging;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;
using HarmonyLib;

namespace SurvivalLog.Mods
{
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
                log.LogInfo("Character enhancement active: move speed x3; exploration exposure limit x5.");
        }

        private static void ScaleMoveSpeed(AttrName attrName, ref float __result)
        {
            if (attrName == AttrName.MoveSpeed)
                __result *= 3f;
        }

        private static void ScaleExposureLimit(ref int __result)
        {
            __result = checked(__result * 5);
        }
    }
}
