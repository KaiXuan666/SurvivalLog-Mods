# 人物增强

合并进统一插件后常驻生效，默认开启：

- 移动速度最终值 ×3（基础速度的 300%）。
- 探索暴露度上限 ×5（基础上限的 500%）。

实现方式是 Harmony postfix 修改读取结果：对人物移动速度 getter 与探索暴露度上限 getter 的返回值应用倍率。这是对**最终读取值**加乘，不是新增限时状态或游戏 buff；其他机制对原值的改动会先进入 getter 结果，再乘以倍率。不改动存档。

依赖的游戏 API：

- `AttributeComponent.GetTotalValue_Float(AttrName)`
- `ExploreManager.MaxExposure`

游戏更新后若上述方法或枚举名称变化，插件会在加载阶段直接抛出 `MissingMethodException` 并记录，不会静默降级。
