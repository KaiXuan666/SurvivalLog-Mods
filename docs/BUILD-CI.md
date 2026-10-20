# CI 打包与发布

`.github/workflows/release.yml` 会构建插件、打包成 zip，并在打标签时自动创建 Release。

## 为什么必须用自托管 runner

编译这个插件要引用游戏生成的两份程序集：

```
BepInEx\interop\HotUpdate.dll        34 MB
BepInEx\interop\Il2Cppmscorlib.dll    6 MB
```

它们是 IL2CPP 从游戏本体生成的程序集，内容就是游戏自己的逻辑代码。因此：

- **不能提交到仓库**：属于游戏版权文件，公开仓库等于分发游戏代码；
- **托管 runner 上没有**：`windows-latest` 没有安装游戏，`dotnet build` 会直接失败。

所以工作流写的是 `runs-on: [self-hosted, Windows, X64]`，在你自己装了游戏的机器上跑。runner 没注册时任务会一直排队（不会报错，容易误判成"卡住"）。

## 注册 runner

1. 打开仓库 **Settings → Actions → Runners → New self-hosted runner**，选择 Windows x64。
2. 按页面给出的脚本执行（在 PowerShell 中，建议放在 `C:\actions-runner`）：

   ```powershell
   mkdir C:\actions-runner; cd C:\actions-runner
   # 下载与解压命令按页面提供的执行
   .\config.cmd --url https://github.com/KaiXuan666/SurvivalLog-Mods --token <页面给的 token>
   ```

   提示输入 runner 名称与标签时，回车接受默认标签（`self-hosted`、`Windows`、`X64`）即可，工作流用的就是这三个。

3. 装成服务，避免每次手动启动：

   ```powershell
   .\svc.cmd install
   .\svc.cmd start
   ```

4. 确认在线：

   ```powershell
   gh api /repos/KaiXuan666/SurvivalLog-Mods/actions/runners --jq '.runners[] | "\(.name) \(.status) \(.labels[].name)"'
   ```

## 指定游戏目录

工作流按以下顺序探测游戏目录，选第一个同时包含两份 interop 的路径：

1. `workflow_dispatch` 的 `game_dir` 输入
2. 环境变量 `SURVIVALLOG_GAME_DIR`
3. 内置默认值 `F:\platform\Steam\steamapps\common\Survival Log`

游戏装在别处时，建议在 **Settings → Secrets and variables → Actions → Variables** 新建仓库变量 `SURVIVALLOG_GAME_DIR`，一次配好长期有效。

## 基底包

完整包需要 HomeButler 官方离线整合包作为基底（内含 BepInEx 6、Doorstop、.NET 运行时、Unity 基础库）。工作流按以下顺序获取，取第一个 SHA-256 匹配的：

1. `workflow_dispatch` 的 `base_package` 输入
2. 环境变量 `HOMEBUTLER_BASE_ZIP`
3. `$RUNNER_TEMP\HomeButler-base.zip`（上次下载的缓存）
4. 按 `HOMEBUTLER_BASE_URL` 下载（默认 v2.0.0，37 MB）

校验值是工作流 `env.HOMEBUTLER_BASE_SHA256`，与上游 release 附带的 `SHA256SUMS.txt` 一致。上游发新版本时需要同时更新 URL 与校验值，否则下载会因哈希不符而中止——这是刻意的，避免悄悄换基底。

runner 上不需要预装 HomeButler，也不需要保留那 5 个 `BaseButler*.dll`；它们从基底包里取。

## 发版流程

```powershell
# 1. 改版本号
#    src\SurvivalLog.Mods\SurvivalLog.Butler.csproj 里的 <Version>

# 2. 提交并打标签
git commit -am "chore: bump version to 1.1.0"
git tag v1.1.0
git push origin main --tags
```

标签推送后：构建 → 打包 → 上传 artifact → 创建 Release 并附上 zip。Release 说明里会自动写入文件名与 SHA256。

只想验证打包、不发布：

```
Actions → Package & Release → Run workflow
  version:        留空（读 csproj）
  publish_release: 不勾选
```

产物在该次运行的 Artifacts 区域。

## zip 内容

产物有两种，都对应游戏根目录：

**完整包** `Survival.Butler-<版本>-full.zip`（约 35 MB，285 个文件）

```
安装说明.txt
先读我.txt
Install.cmd / Uninstall.cmd / Check.cmd / Launch.cmd / ...
release.json
HomeButler-2.0.0-Package/
  manifest.json          ← 已登记本插件
  payload/
    winhttp.dll          ← Doorstop 注入器
    doorstop_config.ini
    .doorstop_version
    dotnet/              ← .NET 6 运行时（hostpolicy、coreclr、System.*）
    BepInEx/core/        ← BepInEx 6 IL2CPP 加载器
    BepInEx/unity-libs/  ← Unity 6000.2.7 基础库（生成 interop 用）
    BepInEx/plugins/
      SurvivalLog.Butler.dll        ← 本插件
      BaseButler*.dll ×5          ← HomeButler
```

用户解压到游戏根目录，运行 `Install.cmd`。安装器（上游 `Setup.ps1`）会：

1. 用 `manifest.json` 的 `PackageFiles` 逐项校验 SHA-256；
2. 按 `Payload` 把文件复制到游戏根目录，记录每个文件的安装前状态；
3. 调用 `PanelLayout.Transform` 把容器脚本注入 10 个游戏页面；
4. 写 `BepInEx.cfg` 的 `[IL2CPP]` 段。

本插件往 `payload/BepInEx/plugins/` 里加了一个 DLL，并在 `Payload`、`PackageFiles` 各补一条记录。上游的安装、卸载、恢复逻辑完全复用。

## 版本校验补丁

打包时会对上游脚本做一次**最小化文本替换**，把版本判断从「阻断」降为「提示」：

| 文件 | 原行为 | 改后 |
| --- | --- | --- |
| `Setup.ps1` | 版本文件缺失 → `throw` | `Write-Warning`，继续 |
| `Setup.ps1` | 游戏版本不符 → `Write-Warning` | `Write-Host` 提示后继续 |
| `Setup.ps1` | Unity 版本不符 → `throw` 中止安装 | `Write-Warning`，继续 |
| `Launch.ps1` | 构建指纹不符 → 进安全模式（禁用 mod） | 跳过指纹比对 |

理由：游戏小版本更新后 mod 通常仍可正常工作，版本号本身不足以判断兼容性。

约束与边界：

- 只替换这 4 个片段，每段要求在上游文件中**恰好出现 1 次**，否则打包直接失败——上游改版时不会静默放过。
- 改动后同步更新 `manifest.json` 里这两个文件的 SHA-256，安装器自身的完整性校验照常通过。
- `Setup.ps1 -Action Check` 失败仍会触发安全模式。它守的是文件结构（页面缺失、路径冲突），与版本号无关，这层保护保留。
- 需要保留上游行为时，打包加 `-KeepVersionChecks`。

改动记录会写入包内 `HomeButler-2.0.0-Package/MODS-PATCH.md`。

**增量包** `Survival.Butler-<版本>-lite.zip`（几十 KB）

```
安装说明.txt
BepInEx/plugins/SurvivalLog.Butler.dll
licenses/
```

只含插件本体，要求游戏目录里已装好 HomeButler 2.0.0。

## 自检

`Package.ps1` 打包后会自动校验，任一项不过就退出码 1，不会产出可发布的包：

- zip 条目全部使用正斜杠、无路径穿越、中文条目名正确；
- 完整包必需文件齐全（Doorstop、BepInEx core、dotnet、unity-libs、5 个 BaseButler）；
- `manifest.json` 的 256 条 `PackageFiles` 与实际文件哈希逐一比对；
- `Payload` 与 `PackageFiles` 都登记了本插件，且记录的哈希与构建产物一致。

## 不想用 runner 的替代方案

本机执行同样的脚本即可，CI 只是把它自动化：

```powershell
.\scripts\Package.ps1                       # 完整包（自动查找基底）
.\scripts\Package.ps1 -BasePackage "D:\...\SurvivalLog-HomeButler-2.0.0-...-OFFLINE.zip"
.\scripts\Package.ps1 -SkipBuild            # 复用已有 DLL，只重新组装
.\scripts\Package.ps1 -Lite                 # 增量包
```

基底包默认在这些位置查找 `SurvivalLog-HomeButler-*-OFFLINE.zip`：`%USERPROFILE%\Downloads`、`%USERPROFILE%\Desktop`、`D:\360极速浏览器X下载`、`C:\Users\Public\Downloads`。也可以用 `-BaseSearchDirs` 指定。

然后把 `dist\*.zip` 手动拖到 Release 资源里。工作流调用的是同一个脚本，两条路径产物一致。

## 脚本说明

| 脚本 | 作用 |
| --- | --- |
| `scripts/Build.ps1` | 查找可用的 .NET SDK（会校验该 dotnet 确实带 SDK），构建 Release |
| `scripts/Package.ps1` | 构建 + 组装暂存目录 + 压缩 + 自检，并向 `GITHUB_OUTPUT` 写入路径与哈希 |
| `scripts/Install.ps1` | 直接安装到本机游戏目录（检测到游戏在运行会中止） |
| `scripts/Get-HomeButler.ps1` | 按清单核对 HomeButler 组件是否完整 |
