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

场景家具提示栏的显示名称会在游戏原生 `FurnitureHeadBar.SetDisplayName` 入口查询 HomeButler 容器目录；家具有自定义名称时显示自定义名和序号，没有则保留游戏原名。

## 注入与安全

- 启动时在 HomeButler `BackpackUI.html` 中插入或更新带标记的脚本，在 `ToolTable.html` 中注入配方右键脚本；
- 注入前校验页面是否包含预期标记；不符合时记录明确错误并中止，不替换整页；
- 注入前的页面备份到 `BepInEx\ContainerPicker-backups\`，以 SHA-256 命名并附带校验值；
- 页面写入采用临时文件替换。
