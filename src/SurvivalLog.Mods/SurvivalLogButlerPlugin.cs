using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace SurvivalLog.Butler
{
    // 统一入口：一个程序集、一份配置，内含人物增强与 HomeButler 容器增强两组功能。
    // HomeButler 为第三方前置依赖，声明为软依赖：未安装时人物增强仍然生效，
    // 容器相关功能跳过并在日志中给出明确提示。
    [BepInPlugin("local.survivallog.butler", "Survival Butler", "1.0.0")]
    [BepInDependency("local.survivallog.homeaccess", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class SurvivalLogButlerPlugin : BasePlugin
    {
        private const string HarmonyId = "local.survivallog.butler";

        public override void Load()
        {
            ModLog.Attach(Log);

            // 设置先绑定：人物增强的补丁会在运行时读这些值，必须先有默认值。
            ButlerSettings.Bind(Config);

            var harmony = new Harmony(HarmonyId);

            // 反射入口补丁：自动归类消息回调、容器改名回调、主界面模组设置消息回调。
            harmony.PatchAll(typeof(SurvivalLogButlerPlugin).Assembly);

            // 人物增强：移动速度与探索暴露度上限按设置里的倍率放大（可在游戏内开关）。
            CharacterEnhancement.Apply(harmony, Log);

            // 容器增强：仅在检测到 HomeButler 页面时注入 WebUI 脚本。
            bool pickerReady = ContainerPickerInstaller.TryInstall(Log, Paths.GameRootPath);

            // 主界面右侧栏的模组按钮与设置面板。
            bool menuReady = ButlerMenu.TryInstall(Log, Paths.GameRootPath);

            // 制作台右键「只看不造」：拦掉 HomeButler 摆完台面后自动开工的那一下。
            ToolTableRecipePeek.Install(harmony);

            Log.LogInfo("[Survival Butler] Loaded. Character enhancement: "
                + (ButlerSettings.EnhanceEnabled ? "on" : "off")
                + " (speed x" + ButlerSettings.MoveSpeedMultiplier + ", exposure x" + ButlerSettings.ExposureMultiplier + ")."
                + " HomeButler container picker: " + (pickerReady ? "on" : "off (HomeButler not detected)")
                + ". Mod settings button: " + (menuReady ? "on" : "off") + ".");
        }
    }
}
