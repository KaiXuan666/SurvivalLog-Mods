#Requires -Version 5.1
<#
打包发布用的 zip。

产物结构对应游戏根目录，用户解压到游戏目录即可（包含 BepInEx 子目录）：
  BepInEx/plugins/SurvivalLog.Mods.dll
  BepInEx/plugins/BaseButler*.dll（HomeButler 前置，随包分发）
  安装说明.txt
  licenses/...

用法：
  .\scripts\Package.ps1
  .\scripts\Package.ps1 -Version 1.0.1 -SkipBuild
#>
[CmdletBinding()]
param(
    [string]$GameDir = "F:\platform\Steam\steamapps\common\Survival Log",
    [string]$Configuration = "Release",
    [string]$Version,
    [string]$OutputDir,
    [switch]$SkipBuild,
    [switch]$NoHomeButler
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\SurvivalLog.Mods\SurvivalLog.Mods.csproj"
$manifestPath = Join-Path $repoRoot "third-party\HomeButler.json"

if (-not $Version) {
    [xml]$proj = Get-Content $project -Raw -Encoding UTF8
    $Version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if (-not $Version) { throw "无法从项目文件读取版本号，请用 -Version 指定。" }
}

if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot "dist" }

# ---- 前置检查 -------------------------------------------------------------
$interop = Join-Path $GameDir "BepInEx\interop\HotUpdate.dll"
if (-not (Test-Path $interop)) {
    throw @"
未找到游戏引用程序集：$interop

编译需要游戏本机生成的 BepInEx\interop（HotUpdate.dll、Il2Cppmscorlib.dll）。
这些文件由游戏本体 IL2CPP 流程生成，不属于本仓库，也无法在云端 runner 上获得。
请在已安装游戏的 Windows 机器上运行本脚本（CI 中即自托管 runner）。
"@
}

# ---- 构建 -----------------------------------------------------------------
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot "Build.ps1") -GameDir $GameDir -Configuration $Configuration
}

$dll = Join-Path $repoRoot "src\SurvivalLog.Mods\bin\$Configuration\net6.0\SurvivalLog.Mods.dll"
if (-not (Test-Path $dll)) { throw "未找到构建输出：$dll" }

# ---- 组装 -------------------------------------------------------------
$stage = Join-Path $OutputDir "stage"
if (Test-Path $stage) { [System.IO.Directory]::Delete($stage, $true) }
$pluginsDir = Join-Path $stage "BepInEx\plugins"
New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null

Copy-Item $dll -Destination $pluginsDir -Force

if (-not $NoHomeButler) {
    $manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($file in $manifest.files) {
        $name = Split-Path $file.path -Leaf
        $source = Join-Path $GameDir "BepInEx\plugins\$name"
        if (-not (Test-Path $source)) {
            throw "缺少 HomeButler 组件：$source。请先安装 HomeButler $($manifest.version)，或用 -NoHomeButler 打包不含前置的版本。"
        }
        $actual = (Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $file.sha256.ToLowerInvariant()) {
            Write-Warning "$name 的 SHA256 与清单不一致（清单 $($manifest.version)），仍继续打包，请在发布说明中注明实际版本。"
        }
        Copy-Item $source -Destination $pluginsDir -Force
    }
}

$licenseDir = Join-Path $stage "licenses"
New-Item -ItemType Directory -Force -Path $licenseDir | Out-Null
foreach ($doc in @("HomeButler-LICENSE.md", "HomeButler-AUTHORIZATION.md")) {
    $src = Join-Path $repoRoot "licenses\$doc"
    if (Test-Path $src) { Copy-Item $src -Destination $licenseDir -Force }
}

$install = @"
SurvivalLog.Mods $Version —— 安装说明
=====================================

前置条件
  · Steam《生存日志》，游戏本体已带 BepInEx IL2CPP（无需另行安装 BepInEx）。
  · 本包已包含 HomeButler 前置组件，无需单独下载。

安装
  1. 完全退出游戏。
  2. 将本压缩包内所有内容解压到游戏根目录，即包含 BepInEx 文件夹的那一层：
       ...\steamapps\common\Survival Log\
     解压后应得到：
       ...\Survival Log\BepInEx\plugins\SurvivalLog.Mods.dll
       ...\Survival Log\BepInEx\plugins\BaseButler*.dll
  3. 启动游戏。首次启动会向背包与制作台页面注入脚本，
     原始页面备份保存在 BepInEx\ContainerPicker-backups\。

从旧版升级
  如果你之前装过分开的插件，请先删除以下两个文件，否则会重复 patch：
       BepInEx\plugins\SurvivalLog.CharacterEnhancement.dll
       BepInEx\plugins\SurvivalLog.ContainerPicker.dll

功能
  · 人物增强：移动速度 x3、探索暴露度上限 x5。
  · 容器增强：背包容器下拉选择、拖放目标、自动归类、制作台配方右键。

排查
  查看 BepInEx\LogOutput.log，搜索 "SurvivalLog.Mods"。
  未检测到 HomeButler 时，日志会提示容器功能已跳过，人物增强仍然生效。

第三方组件
  HomeButler 2.0.0（BaseButler）由 lingxiangtingsheng 创作、lingbaicat 维护，
  其许可条款见 licenses\HomeButler-LICENSE.md，收录授权见 licenses\HomeButler-AUTHORIZATION.md。
  本包不授予游戏代码、素材及游戏生成程序集的使用权。
"@
Set-Content -Path (Join-Path $stage "安装说明.txt") -Value $install -Encoding UTF8

# ---- 压缩 -------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$zipName = "SurvivalLog.Mods-$Version.zip"
$zipPath = Join-Path $OutputDir $zipName

# -Force 覆盖同名产物，不做删除
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath -CompressionLevel Optimal -Force

$sizeKb = [math]::Round((Get-Item $zipPath).Length / 1KB, 1)
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host ""
Write-Host "打包完成: $zipPath" -ForegroundColor Green
Write-Host "大小: $sizeKb KB" -ForegroundColor Green
Write-Host "SHA256: $hash" -ForegroundColor Green
Write-Host ""
Write-Host "内容:" -ForegroundColor Cyan
Get-ChildItem $stage -Recurse -File | ForEach-Object {
    "  " + $_.FullName.Substring($stage.Length + 1)
}

# 供 CI 读取（显式 UTF-8 无 BOM，避免 Windows PowerShell 5.1 写入 BOM 影响解析）
if ($env:GITHUB_OUTPUT) {
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    $lines = @(
        "zip_path=$zipPath",
        "zip_name=$zipName",
        "zip_sha256=$hash",
        "version=$Version"
    ) -join "`n"
    [System.IO.File]::AppendAllText($env:GITHUB_OUTPUT, $lines + "`n", $utf8NoBom)
}
