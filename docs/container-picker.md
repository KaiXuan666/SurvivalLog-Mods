# HomeButler 容器增强

依赖 `local.survivallog.homeaccess`（HomeButler 2.0.0），未安装时本组功能自动跳过。

## 容器下拉选择

在 BackpackUI 顶部分类按钮左侧添加下拉箭头。菜单使用 HomeButler 已提供的容器标签数据，选择后调用对应原版标签点击事件。

## 自动归类

在每个容器面板底部（A、B 两侧的一键整理按钮旁）加入「自动归类」按钮。不提供自定义分类设置；按钮所在面板的当前容器作为来源，其他可访问容器作为目标。

执行流程：

1. 读取 BackpackUI 页签以及当前 B 容器的 owner，确定来源容器；
2. 目标范围 = 当前页签及已打开的 B 容器中可访问、且设有官方标签规则的容器（排除来源）；
3. 先对每个目标调用 `ItemManager.SortContainerForHub(owner)` 整理空闲格位；
4. 再对每个目标调用 `ItemManager.ExecuteMoveBag(sourceOwner, targetOwner, 0, false, false, tagRule)`；
5. 按来源容器转移前后的物品数量差生成明细。

标签匹配、目标容量、负重和存入限制由游戏原生逻辑负责；某个目标无法接收时继续尝试其他目标。结果以非交互 toast 提示，未能转移的物品逐项显示名称、数量和原因。

来源可为 A 侧当前页签或 B 侧当前容器；每个显示中的容器面板底部都有独立按钮。

## 页签拖放桥接

拖动中的箭头菜单项接入原生 `createTabDrop`：拖到箭头展开菜单，再拖到目标项并松手即可尝试转移，复用游戏的 `DROP_TO_TAB` 流程及其限制判定。

`ContainerPickerTabDropBridge.js` 必须在 `createTabDrop` 初始化前加载——注入顺序为：`webui-tabdrop.js` 之后、BackpackUI 主脚本之前。

## 制作台配方右键

ToolTable 制作笔记中的配方支持右键选择：通过原生配方选择事件将配方装入制作台，使用原版预览区提供的染色选项（`DYE_PICK`）；左键行为保持不变。

## 场景家具名称

长按 Alt 显示的场景名称来自游戏自己的名称栏（`FurnitureHeadBar`），它读的是家具配置里的原名，与 HomeButler 的自定义名称没有任何关系。插件在名称栏的渲染边界接管：

- 挂接 `FurnitureHeadBar.SetName`、`SetDisplayName`、`ApplyNameWithSearchCount` 与 `OnUpdate`，任一入口写入文本后都用自定义名覆盖；
- 同时把名称栏的 `_baseName` 改成自定义名，游戏后续重排（如搜索计数 `名称 (12)`）会沿用自定义名，只替换名称部分、保留后缀；
- `OnUpdate` 每帧回写、每 16 帧重新解析目录，改名后约 0.3 秒内场景同步，不需要重新进出场景；
- 家具定位依次尝试名称栏 `Target` 自身、向上最多 8 层父节点、直接子节点，最后退回 `showInstanceId` 到 `AgentManager.GetAgent` 取逻辑对象——名称栏挂在模型子节点时也能命中；
- 目录按存档内稳定实例 id 存，场景里是运行时实例 id，通过 HomeButler 的 `loadedIds` 映射换算；同一实例换了家具类型时不沿用旧名。

日志里搜索 `[SceneNames]` 可看到挂接了哪些方法、哪些家具命中了自定义名。HomeButler 缺失时本功能自动静默跳过，家具仍显示原名。

## 注入与安全

- 启动时在 HomeButler `BackpackUI.html` 中插入或更新带标记的脚本，在 `ToolTable.html` 中注入配方右键脚本；
- 注入前校验页面是否包含预期标记；不符合时记录明确错误并中止，不替换整页；
- 注入前的页面备份到 `BepInEx\ContainerPicker-backups\`，以 SHA-256 命名并附带校验值；
- 页面写入采用临时文件替换。
