using BepInEx.Configuration;

namespace SurvivalLog.Butler
{
    // 插件设置：游戏内「模组设置」面板与 BepInEx\config 配置文件共用这一份数据。
    // 面板改动会立即写回 cfg 文件，所以下次启动、或直接手改 cfg 都保持一致。
    internal static class ButlerSettings
    {
        private static ConfigEntry<bool> _enhanceEnabled;
        private static ConfigEntry<float> _moveSpeedMultiplier;
        private static ConfigEntry<float> _exposureMultiplier;

        // 倍率上下限：面板滑条与配置文件都用这组值约束，避免手改成 0 或天文数字把游戏搞坏。
        internal const float MinMultiplier = 1f;
        internal const float MaxMultiplier = 20f;

        internal static void Bind(ConfigFile config)
        {
            _enhanceEnabled = config.Bind(
                "CharacterEnhancement", "Enabled", true,
                "是否启用人物增强。关闭后移动速度与探索暴露度恢复游戏原始数值，无需重启。");

            _moveSpeedMultiplier = config.Bind(
                "CharacterEnhancement", "MoveSpeedMultiplier", 3f,
                new ConfigDescription(
                    "移动速度倍率。1 表示保持原版。",
                    new AcceptableValueRange<float>(MinMultiplier, MaxMultiplier)));

            _exposureMultiplier = config.Bind(
                "CharacterEnhancement", "ExposureLimitMultiplier", 5f,
                new ConfigDescription(
                    "探索暴露度上限倍率。1 表示保持原版。",
                    new AcceptableValueRange<float>(MinMultiplier, MaxMultiplier)));
        }

        internal static bool EnhanceEnabled
        {
            get { return _enhanceEnabled != null && _enhanceEnabled.Value; }
            set
            {
                if (_enhanceEnabled == null || _enhanceEnabled.Value == value) return;
                _enhanceEnabled.Value = value;
                ModLog.Info("[Settings] Character enhancement " + (value ? "enabled." : "disabled."));
            }
        }

        internal static float MoveSpeedMultiplier
        {
            get { return _moveSpeedMultiplier == null ? 1f : Clamp(_moveSpeedMultiplier.Value); }
            set
            {
                if (_moveSpeedMultiplier == null) return;
                _moveSpeedMultiplier.Value = Clamp(value);
            }
        }

        internal static float ExposureMultiplier
        {
            get { return _exposureMultiplier == null ? 1f : Clamp(_exposureMultiplier.Value); }
            set
            {
                if (_exposureMultiplier == null) return;
                _exposureMultiplier.Value = Clamp(value);
            }
        }

        private static float Clamp(float value)
        {
            if (value < MinMultiplier) return MinMultiplier;
            if (value > MaxMultiplier) return MaxMultiplier;
            // 保留两位小数：面板滑条是整数刻度，但手改 cfg 可能写进 3.14159，统一一下避免面板显示怪异。
            return (float)System.Math.Round(value, 2);
        }
    }
}
