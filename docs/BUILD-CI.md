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

另外要确保 runner 账号能读到 `BepInEx\plugins\` 下的 5 个 `BaseButler*.dll`——打包会把它们一并放进 zip。

## 发版流程

```powershell
# 1. 改版本号
#    src\SurvivalLog.Mods\SurvivalLog.Mods.csproj 里的 <Version>

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

解压到游戏根目录即可生效（`BepInEx/` 直接对应游戏下的同名目录）：

```
安装说明.txt
BepInEx/
  plugins/
    SurvivalLog.Mods.dll
    BaseButler.dll
    BaseButler.HomeAccess.dll
    BaseButler.ContainerNavigation.dll
    BaseButler.ContainerInlineEditor.dll
    BaseButler.NativeCompatibility.dll
licenses/
  HomeButler-LICENSE.md
  HomeButler-AUTHORIZATION.md
```

## 不想用 runner 的替代方案

本机执行同样的脚本即可，CI 只是把它自动化：

```powershell
.\scripts\Package.ps1                      # 构建 + 打包到 dist\
.\scripts\Package.ps1 -SkipBuild           # 复用已有 DLL
.\scripts\Package.ps1 -NoHomeButler        # 不捆绑前置，产物体积更小
```

然后把 `dist\SurvivalLog.Mods-<版本>.zip` 手动拖到 Release 资源里。工作流调用的是同一个脚本，两条路径产物一致。

## 脚本说明

| 脚本 | 作用 |
| --- | --- |
| `scripts/Build.ps1` | 查找可用的 .NET SDK（会校验该 dotnet 确实带 SDK），构建 Release |
| `scripts/Package.ps1` | 构建 + 组装暂存目录 + 压缩 zip，并向 `GITHUB_OUTPUT` 写入路径与哈希 |
| `scripts/Install.ps1` | 直接安装到本机游戏目录（检测到游戏在运行会中止） |
| `scripts/Get-HomeButler.ps1` | 按清单核对 HomeButler 组件是否完整 |
