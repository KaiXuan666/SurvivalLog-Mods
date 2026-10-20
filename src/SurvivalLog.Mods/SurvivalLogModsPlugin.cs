using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace SurvivalLog.Mods
{
    // 统一入口：一个程序集、一份配置，内含人物增强与 HomeButler 容器增强两组功能。
    // HomeButler 为第三方前置依赖，声明为软依赖：未安装时人物增强仍然生效，
    // 容器相关功能跳过并在日志中给出明确提示。
    [BepInPlugin("local.survivallog.mods", "Survival Log Mods", "1.0.0")]
    [BepInDependency("local.survivallog.homeaccess", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class SurvivalLogModsPlugin : BasePlugin
    {
        private const string HarmonyId = "local.survivallog.mods";

        public override void Load()
        {
            ModLog.Attach(Log);
            var harmony = new Harmony(HarmonyId);

            // 反射入口补丁：自动归类消息回调。
            harmony.PatchAll(typeof(SurvivalLogModsPlugin).Assembly);

            // 场景名称栏：把 HomeButler 的自定义容器名接到长按 Alt 显示的名称上。
            SceneContainerNames.Install(harmony, Log);

            // 人物增强：移动速度 x3、探索暴露度上限 x5。
            CharacterEnhancement.Apply(harmony, Log);

            // 容器增强：仅在检测到 HomeButler 页面时注入 WebUI 脚本。
            bool pickerReady = ContainerPickerInstaller.TryInstall(Log, Paths.GameRootPath);

            // 制作台右键「只看不造」：拦掉 HomeButler 摆完台面后自动开工的那一下。
            ToolTableRecipePeek.Install(harmony);
            Log.LogInfo("[SurvivalLog.Mods] Loaded. Character enhancement: on. HomeButler container picker: "
                + (pickerReady ? "on" : "off (HomeButler not detected)") + ".");
        }
    }
}
