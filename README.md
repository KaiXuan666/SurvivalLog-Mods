# SurvivalLog-Mods

《生存日志》（Survival Log）的一体化 BepInEx IL2CPP 插件。

此前分散维护的三个 MOD 已合并为**一个插件、一个程序集、一份配置**：

| 原 MOD | 合并后的功能 |
| --- | --- |
| 人物增强 | 移动速度 ×3、探索暴露度上限 ×5 |
| HomeButler 容器下拉选择 | 容器下拉箭头与菜单、页签拖放桥接、自动归类、制作台配方右键 |
| HomeButler | **第三方前置依赖**，源码收录于 `third-party/homebutler/`，编译产物不随仓库分发（见下文） |

## 前置条件

1. Steam 版《生存日志》，且已由游戏本体带好 BepInEx IL2CPP。
2. `BepInEx/interop` 已生成（`AttributeComponent`、`ExploreManager`、`ItemManager`、`FurnitureTagComponent`、`WebUIVm`、`WebUILayer` 等 IL2CPP 接口）。游戏更新后需重新核对这些 API。
3. **HomeButler 2.0.0**（`local.survivallog.homeaccess`）：使用 Release 的 zip 时已内置；从源码构建时需自行安装。

### 关于 HomeButler

HomeButler 由原作者 lingxiangtingsheng 创作、维护者 lingbaicat 适配整合。本仓库对其内容的收录范围：

| 内容 | 是否收录 | 说明 |
| --- | --- | --- |
| `source/` 源码 | ✅ 收录 | 经原作者 lingxiangtingsheng 与维护者 lingbaicat 许可，位于 `third-party/homebutler/source/` |
| 上游 README、CHANGELOG | ✅ 收录 | 保留原始说明与变更记录 |
| 原始许可文本 | ✅ 收录 | [`licenses/HomeButler-LICENSE.md`](licenses/HomeButler-LICENSE.md) |
| 编译产物（5 个 `BaseButler*.dll`） | ❌ 仓库不收录 | 但会随 Release 的 zip 分发（经授权），便于解压即用；仓库内仅以 [`third-party/HomeButler.json`](third-party/HomeButler.json) 的 SHA-256 记录版本 |
| 安装器与运行时（`Setup.ps1`、`Launch.ps1`、`payload/`、`manifest.json`） | ❌ 不收录 | 属于打包与部署产物，非源码 |

授权依据见 [`licenses/HomeButler-AUTHORIZATION.md`](licenses/HomeButler-AUTHORIZATION.md)。上游地址：<https://github.com/lingbaicat/SurvivalLog-HomeButler-lingbaicat>

注意：仓库收录源码**不等于**将其重新许可。HomeButler 部分仍适用其原始条款，本仓库未对其授予任何额外权利。

## 安装（推荐：下载 zip）

从 [Releases](https://github.com/KaiXuan666/SurvivalLog-Mods/releases) 下载 `SurvivalLog.Mods-<版本>.zip`：

1. 完全退出游戏；
2. 解压到游戏根目录——即包含 `BepInEx` 文件夹的那一层 `...\steamapps\common\Survival Log\`；
3. 启动游戏。

包内已包含 HomeButler 的 5 个组件，无需另行下载。**如果你之前装过分开的插件**，先删除 `BepInEx\plugins\SurvivalLog.CharacterEnhancement.dll` 与 `BepInEx\plugins\SurvivalLog.ContainerPicker.dll`，否则会重复 patch。

## 从源码构建与安装

需要 .NET 6 SDK。若 `dotnet` 不在 PATH 中，脚本会自动查找带 SDK 的安装位置：

```powershell
.\scripts\Build.ps1          # 只构建
.\scripts\Package.ps1        # 构建并打包 dist\SurvivalLog.Mods-<版本>.zip
```

或手动构建：

```powershell
dotnet build .\src\SurvivalLog.Mods\SurvivalLog.Mods.csproj -c Release
```

游戏安装目录不同时用 `-p:GameDir="完整路径"` 覆盖（脚本对应参数为 `-GameDir`）。

**先关闭游戏**，再把 `SurvivalLog.Mods.dll` 放进 `BepInEx\plugins\`；或直接运行：

```powershell
.\scripts\Install.ps1
```

脚本会构建 Release 并复制到游戏目录，检测到游戏正在运行时会直接中止，不覆盖正在使用的文件。插件更新需重启游戏生效。

## 自动打包

`.github/workflows/release.yml` 负责构建、打包并在打 `v*` 标签时创建 Release。由于编译依赖游戏本机生成的 `BepInEx\interop`（不能入库、云端也没有），工作流使用**自托管 runner**。注册方式、游戏目录配置与发版流程见 [docs/BUILD-CI.md](docs/BUILD-CI.md)。


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
