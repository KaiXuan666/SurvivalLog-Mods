# SurvivalLog-Mods

《生存日志》（Survival Log）的一体化 BepInEx IL2CPP 插件。

此前分散维护的三个 MOD 已合并为**一个插件、一个程序集、一份配置**：

| 原 MOD | 合并后的功能 |
| --- | --- |
| 人物增强 | 移动速度 ×3、探索暴露度上限 ×5 |
| HomeButler 容器下拉选择 | 容器下拉箭头与菜单、页签拖放桥接、自动归类、制作台配方右键 |
| HomeButler | **第三方前置依赖，不在此仓库分发**（见下文） |

## 前置条件

1. Steam 版《生存日志》，且已由游戏本体带好 BepInEx IL2CPP。
2. `BepInEx/interop` 已生成（`AttributeComponent`、`ExploreManager`、`ItemManager`、`FurnitureTagComponent`、`WebUIVm`、`WebUILayer` 等 IL2CPP 接口）。游戏更新后需重新核对这些 API。
3. 已安装 **HomeButler 2.0.0**（`local.survivallog.homeaccess`）。

### 关于 HomeButler

HomeButler 由原作者 lingxiangtingsheng 创作、维护者 lingbaicat 适配整合，其授权条款明确**未授予任意修改、商业使用或再分发权**。因此本仓库：

- 不包含 HomeButler 的源码或编译产物（DLL）；
- 仅在 [`third-party/HomeButler.json`](third-party/HomeButler.json) 中记录本机已安装版本的来源、文件大小与 SHA-256，用于核对；
- 保留原始许可文本 [`licenses/HomeButler-LICENSE.md`](licenses/HomeButler-LICENSE.md) 以完成署名。

请前往上游地址获取：<https://github.com/lingbaicat/SurvivalLog-HomeButler-lingbaicat>

如已取得维护者的书面再分发授权，可将 HomeButler 源码放入 `third-party/homebutler/` 后再行提交。

## 构建

需要 .NET 6 SDK。若 `dotnet` 不在 PATH 中，脚本会自动查找常见安装位置：

```powershell
.\scripts\Build.ps1
```

或手动构建：

```powershell
dotnet build .\src\SurvivalLog.Mods\SurvivalLog.Mods.csproj -c Release
```

游戏安装目录不同时用 `-p:GameDir="完整路径"` 覆盖（脚本对应参数为 `-GameDir`）。

## 安装

**先关闭游戏**，再执行：

```powershell
.\scripts\Install.ps1
```

脚本会构建 Release，并把 `SurvivalLog.Mods.dll` 复制到游戏目录的 `BepInEx\plugins\`。检测到游戏正在运行时会直接中止，不覆盖正在使用的文件。插件更新需重启游戏生效。

## 功能说明

- 人物增强：[docs/character-enhancement.md](docs/character-enhancement.md)
- 容器增强：[docs/container-picker.md](docs/container-picker.md)

## 行为约定

- HomeButler 声明为**软依赖**：未安装时人物增强照常生效，容器相关功能跳过并在 `BepInEx\LogOutput.log` 中给出明确提示，不静默失败。
- WebUI 注入前会校验目标页面是否包含预期的 HomeButler 标记；页面结构或版本标记不符时中止注入并写日志，不盲目替换页面。
- 注入前的原始页面会以 SHA-256 命名并附带校验值备份到 `BepInEx\ContainerPicker-backups\`，写入采用临时文件替换。
- 自动归类走游戏原生接口：先用 `ItemManager.SortContainerForHub(owner)` 整理目标，再以 `ItemManager.ExecuteMoveBag(..., tagRule)` 转移，标签匹配、容量、负重与存入限制全部由游戏原生逻辑判定。

## 仓库许可

本仓库中**自研部分**（`src/`、`scripts/`）尚未指定开源许可证，默认保留全部权利；如需开源请自行添加 LICENSE。第三方组件的权利见 `licenses/`。
