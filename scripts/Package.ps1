#Requires -Version 5.1
<#
生成发布用的 zip。有两种模式：

  完整安装包（默认）
    以 HomeButler 官方离线整合包为基底，把本插件并入它的 payload 与 manifest，
    产出「解压到游戏根目录 + 运行 Install.cmd」即可用的一体化安装包。
    基底包含 BepInEx 6 (IL2CPP)、Doorstop、.NET 运行时与 Unity 基础库——
    这些是插件运行的必需条件，单独一个 DLL 在干净游戏上不会被加载。

  增量包（-Lite）
    只含 BepInEx\plugins\SurvivalLog.Mods.dll，供已装好 HomeButler 的用户升级。

用法：
  .\scripts\Package.ps1
  .\scripts\Package.ps1 -BasePackage "D:\...\SurvivalLog-HomeButler-2.0.0-Game1.1.18599-win-x64-OFFLINE.zip"
  .\scripts\Package.ps1 -Lite -SkipBuild
#>
[CmdletBinding()]
param(
    [string]$GameDir = "F:\platform\Steam\steamapps\common\Survival Log",
    [string]$Configuration = "Release",
    [string]$Version,
    [string]$OutputDir,
    [string]$BasePackage,
    [string[]]$BaseSearchDirs,
    [switch]$SkipBuild,
    [switch]$Lite
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\SurvivalLog.Mods\SurvivalLog.Mods.csproj"
$manifestPath = Join-Path $repoRoot "third-party\HomeButler.json"
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$utf8Bom = [System.Text.UTF8Encoding]::new($true)

# ---- 版本 -----------------------------------------------------------------
if (-not $Version) {
    [xml]$proj = Get-Content $project -Raw -Encoding UTF8
    $Version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if (-not $Version) { throw "无法从项目文件读取版本号，请用 -Version 指定。" }
}
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot "dist" }

# ---- 压缩支持 -------------------------------------------------------------
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Expand-Zip([string]$ZipPath, [string]$Destination) {
    # 用 .NET API 并显式指定 UTF-8，保证「先读我.txt」等中文条目名正确还原。
    # Expand-Archive 在 PowerShell 5.1 下会按 ANSI 代码页解释条目名。
    $enc = [System.Text.Encoding]::UTF8
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $Destination, $enc)
}

function New-ZipFromDirectory([string]$SourceDir, [string]$ZipPath) {
    # 不用 Compress-Archive：PowerShell 5.1 的实现在条目名里写反斜杠，
    # 跨平台解压与部分第三方解压工具会把它当成文件名的一部分。
    # 这里手工建条目，路径分隔符统一为正斜杠。
    if (Test-Path -LiteralPath $ZipPath) { [System.IO.File]::Delete($ZipPath) }

    $fs = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::CreateNew)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive(
            $fs,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false,
            [System.Text.Encoding]::UTF8)
        try {
            $base = [System.IO.Path]::GetFullPath($SourceDir).TrimEnd('\') + '\'
            foreach ($file in (Get-ChildItem -LiteralPath $SourceDir -Recurse -File -Force)) {
                $rel = $file.FullName.Substring($base.Length).Replace('\', '/')
                $entry = $archive.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $file.LastWriteTime
                $out = $entry.Open()
                try {
                    $in = [System.IO.File]::OpenRead($file.FullName)
                    try { $in.CopyTo($out, 81920) } finally { $in.Dispose() }
                }
                finally { $out.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $fs.Dispose() }
}

function Find-BasePackage {
    if ($BaseSearchDirs) { $dirs = $BaseSearchDirs }
    else {
        $dirs = @(
            (Join-Path ([Environment]::GetFolderPath('UserProfile')) "Downloads"),
            (Join-Path ([Environment]::GetFolderPath('UserProfile')) "Desktop"),
            "D:\360极速浏览器X下载",
            "C:\Users\Public\Downloads"
        )
    }
    $candidates = @()
    foreach ($d in $dirs) {
        if (-not (Test-Path $d)) { continue }
        $candidates += Get-ChildItem -Path $d -Filter "SurvivalLog-HomeButler-*-OFFLINE.zip" -File -ErrorAction SilentlyContinue
    }
    if (-not $candidates) { return $null }
    return ($candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}

# ---- 构建 -----------------------------------------------------------------
$dll = Join-Path $repoRoot "src\SurvivalLog.Mods\bin\$Configuration\net6.0\SurvivalLog.Mods.dll"

if (-not $SkipBuild) {
    $interop = Join-Path $GameDir "BepInEx\interop\HotUpdate.dll"
    if (-not (Test-Path $interop)) {
        throw @"
未找到游戏引用程序集：$interop

编译需要游戏本机生成的 BepInEx\interop（HotUpdate.dll、Il2Cppmscorlib.dll）。
这些文件由游戏本体的 IL2CPP 流程生成，不属于本仓库，也无法在云端 runner 上获得。

若只想用已有产物重新打包，请加 -SkipBuild。
"@
    }
    & (Join-Path $PSScriptRoot "Build.ps1") -GameDir $GameDir -Configuration $Configuration
}
if (-not (Test-Path $dll)) { throw "未找到构建输出：$dll（先运行不带 -SkipBuild 的打包，或先 Build.ps1）" }
$dllHash = Get-Sha256 $dll
$dllSize = (Get-Item $dll).Length

# ---- 组装 -----------------------------------------------------------------
$stage = Join-Path $OutputDir "stage"
if (Test-Path $stage) { [System.IO.Directory]::Delete($stage, $true) }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$installNotes = @"
SurvivalLog.Mods $Version —— 安装说明
=====================================

本插件运行在 BepInEx 6 (IL2CPP) 之上，并依赖 HomeButler 2.0.0 提供的容器接口。
单独一个 DLL 放进干净的游戏目录不会被加载，因此本包按下面的方式提供。

安装（三步，与官方 HomeButler 包一致）
  1. 完全退出游戏。
  2. 把本压缩包内所有内容解压到游戏根目录，即含 SurvivalLog.exe 的那一层：
       ...\steamapps\common\Survival Log\
     解压后应有：
       ...\Survival Log\安装说明.txt（本文件）
       ...\Survival Log\HomeButler-2.0.0-Package\（保留，卸载时用）
       ...\Survival Log\BepInEx\plugins\SurvivalLog.Mods.dll
  3. 双击 Install.cmd，确认显示安装成功。

  Install.cmd 会写入 Doorstop 与 BepInEx 配置、注入页面脚本。
  首次启动游戏时 BepInEx 需要生成 IL2CPP 接口，可能耗时数分钟；
  本次启动通常还不会加载插件，再次启动后才生效。

从旧版升级
  若之前装过分开的两个插件，先删除下面两个文件，否则会重复 patch：
       BepInEx\plugins\SurvivalLog.CharacterEnhancement.dll
       BepInEx\plugins\SurvivalLog.ContainerPicker.dll

  若之前已装过 HomeButler 官方包，直接解压本包并再次运行 Install.cmd 即可，
  安装器会跳过内容相同的文件。

功能
  · 人物增强：移动速度 x3、探索暴露度上限 x5。
  · 容器增强：背包容器下拉选择、拖放目标、自动归类、制作台配方右键。

排查
  用 ViewLog.cmd 打开 BepInEx\LogOutput.log，搜索 "SurvivalLog.Mods"。
  未检测到 HomeButler 页面标记时，容器功能会跳过并在日志中说明，人物增强仍生效。

卸载
  运行 Uninstall.cmd 撤销安装。它按安装记录恢复原文件，不会删除存档与个人配置。

第三方组件
  本包内含 BepInEx 6 (IL2CPP)、Doorstop、.NET 运行时与 Unity 基础库，
  均由上游 HomeButler 离线整合包提供，各自许可见 HomeButler-2.0.0-Package\licenses\
  与 THIRD-PARTY-NOTICES.md。
  HomeButler（BaseButler）由 lingxiangtingsheng 创作、lingbaicat 维护；
  本仓库对源码的收录授权见 licenses\HomeButler-AUTHORIZATION.md。
  本包不授予游戏代码、素材及游戏生成程序集的使用权。
"@

$pkgDir = $null

if ($Lite) {
    # 增量包：只放插件本体，结构与游戏根目录一致
    $pluginsDir = Join-Path $stage "BepInEx\plugins"
    New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null
    Copy-Item $dll -Destination $pluginsDir -Force

    $licDir = Join-Path $stage "licenses"
    New-Item -ItemType Directory -Force -Path $licDir | Out-Null
    foreach ($doc in @("HomeButler-LICENSE.md", "HomeButler-AUTHORIZATION.md")) {
        $src = Join-Path $repoRoot "licenses\$doc"
        if (Test-Path $src) { Copy-Item $src -Destination $licDir -Force }
    }

    $installNotes = @"
SurvivalLog.Mods $Version —— 增量包安装说明
==========================================
★ 这是增量包，不是完整安装包 ★

前置条件：游戏目录里已经装好 HomeButler 2.0.0，
即存在 BepInEx\core\ 与 BepInEx\plugins\BaseButler.dll。
未装前置时，本包里的 DLL 不会被加载，也不会有任何提示。

安装
  1. 完全退出游戏。
  2. 把本压缩包内所有内容解压到游戏根目录，覆盖同名文件：
       ...\steamapps\common\Survival Log\
  3. 启动游戏。页面注入已由 HomeButler 的 Install.cmd 完成，无需重跑。

需要「解压即用」的完整包装？请下载文件名带 -full 的版本。

从旧版升级
  若之前装过分开的两个插件，先删除下面两个文件，否则会重复 patch：
       BepInEx\plugins\SurvivalLog.CharacterEnhancement.dll
       BepInEx\plugins\SurvivalLog.ContainerPicker.dll

功能
  · 人物增强：移动速度 x3、探索暴露度上限 x5。
  · 容器增强：背包容器下拉选择、拖放目标、自动归类、制作台配方右键。

排查
  查看 BepInEx\LogOutput.log，搜索 "SurvivalLog.Mods"。

第三方组件
  HomeButler（BaseButler）由 lingxiangtingsheng 创作、lingbaicat 维护；
  许可见 licenses\HomeButler-LICENSE.md，收录授权见 licenses\HomeButler-AUTHORIZATION.md。
"@
}
else {
    # 完整包：以官方离线整合包为基底
    if (-not $BasePackage) { $BasePackage = Find-BasePackage }
    if (-not $BasePackage -or -not (Test-Path $BasePackage)) {
        throw @"
未找到 HomeButler 官方离线整合包作为基底。

本插件运行需要 BepInEx 6 (IL2CPP) + Doorstop + .NET 运行时 + Unity 基础库，
这些由 HomeButler 官方离线包提供。请下载后重试，或用 -BasePackage 指定路径：

  https://github.com/lingbaicat/SurvivalLog-HomeButler-lingbaicat/releases

只想产出不含基底的增量包时，加 -Lite。
"@
    }

    Write-Host "基底包: $BasePackage" -ForegroundColor Cyan
    Expand-Zip -ZipPath $BasePackage -Destination $stage

    $pkgDir = Get-ChildItem -Path $stage -Directory -Filter "HomeButler-*-Package" | Select-Object -First 1
    if (-not $pkgDir) { throw "基底包结构异常：未找到 HomeButler-*-Package 目录。" }

    $payloadPlugins = Join-Path $pkgDir.FullName "payload\BepInEx\plugins"
    if (-not (Test-Path $payloadPlugins)) { throw "基底包结构异常：未找到 payload\BepInEx\plugins。" }

    Copy-Item $dll -Destination $payloadPlugins -Force

    # --- 更新 manifest：Payload 与 PackageFiles 各加一条 ---
    $mPath = Join-Path $pkgDir.FullName "manifest.json"
    $manifest = Get-Content $mPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $relSource = "payload/BepInEx/plugins/SurvivalLog.Mods.dll"
    $relTarget = "BepInEx\plugins\SurvivalLog.Mods.dll"

    $manifest.Payload = @(@($manifest.Payload) | Where-Object { $_.Target -ne $relTarget }) +
        [pscustomobject]@{ Target = $relTarget; Source = $relSource }

    $manifest.PackageFiles = @(@($manifest.PackageFiles) | Where-Object { $_.Path -ne $relSource }) +
        [pscustomobject]@{ Path = $relSource; SHA256 = $dllHash }

    [System.IO.File]::WriteAllText($mPath, ($manifest | ConvertTo-Json -Depth 20), $utf8NoBom)

    # --- 追加说明到「先读我.txt」（保持原有 BOM） ---
    $readmePath = Join-Path $stage "先读我.txt"
    if (Test-Path $readmePath) {
        $extra = @"

────────────────────────────────────────────────────────────────
本包已加入 SurvivalLog.Mods $Version（人物增强 + 容器增强整合插件）
────────────────────────────────────────────────────────────────
安装步骤与上文「三步安装」完全相同，无需额外操作。
插件源文件：HomeButler-2.0.0-Package\payload\BepInEx\plugins\SurvivalLog.Mods.dll
安装后位置：BepInEx\plugins\SurvivalLog.Mods.dll
功能：移动速度 x3、探索暴露度上限 x5；背包容器下拉选择、拖放目标、
      自动归类、制作台配方右键。
排查：ViewLog.cmd 打开 BepInEx\LogOutput.log，搜索 "SurvivalLog.Mods"。
卸载：运行 Uninstall.cmd 会一并撤销本插件。
许可：HomeButler-2.0.0-Package\licenses\ 与本包 licenses\ 目录。
"@
        [System.IO.File]::AppendAllText($readmePath, $extra, $utf8Bom)
    }

    # --- release.json 标注整合来源 ---
    $relPath = Join-Path $stage "release.json"
    if (Test-Path $relPath) {
        $rel = Get-Content $relPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $rel | Add-Member -NotePropertyName "mods" -NotePropertyValue ([pscustomobject]@{
            name    = "SurvivalLog.Mods"
            version = $Version
            source  = "https://github.com/KaiXuan666/SurvivalLog-Mods"
        }) -Force
        [System.IO.File]::WriteAllText($relPath, ($rel | ConvertTo-Json -Depth 10), $utf8NoBom)
    }
}

[System.IO.File]::WriteAllText((Join-Path $stage "安装说明.txt"), $installNotes, $utf8Bom)

# ---- 打包 -------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$edition = if ($Lite) { "lite" } else { "full" }
$zipName = "SurvivalLog.Mods-$Version-$edition.zip"
$zipPath = Join-Path $OutputDir $zipName

# Compress-Archive 在 PowerShell 5.1 下写反斜杠条目名，这里用自建条目（见 New-ZipFromDirectory）
New-ZipFromDirectory -SourceDir $stage -ZipPath $zipPath

# ---- 自检 -------------------------------------------------------------
$fail = New-Object System.Collections.Generic.List[string]
$warn = New-Object System.Collections.Generic.List[string]

Add-Type -AssemblyName System.IO.Compression
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entries = @($zip.Entries | Where-Object { $_.Name -ne "" })
    if ($entries.Count -eq 0) { $fail.Add("zip 为空") }

    foreach ($e in $entries) {
        if ($e.FullName.Contains("\")) { $fail.Add("条目使用反斜杠: $($e.FullName)") }
        if ($e.FullName -match "(^|/)\.\.(/|$)") { $fail.Add("条目含路径穿越: $($e.FullName)") }
    }

    $names = @($entries | ForEach-Object { $_.FullName })

    if (-not ($names -contains "安装说明.txt")) { $fail.Add("缺少 安装说明.txt") }

    if ($Lite) {
        if (-not ($names -contains "BepInEx/plugins/SurvivalLog.Mods.dll")) { $fail.Add("缺少插件 DLL") }
    }
    else {
        # 完整包里，基底文件位于 HomeButler-<ver>-Package/payload/ 下，
        # 由 Install.cmd 按 manifest 复制到游戏根目录，所以自检要看 payload 前缀。
        $prefix = $pkgDir.Name + "/payload/"
        $required = @(
            "winhttp.dll",
            "doorstop_config.ini",
            ".doorstop_version",
            "BepInEx/core/BepInEx.Preloader.Core.dll",
            "BepInEx/core/BepInEx.Unity.IL2CPP.dll",
            "dotnet/coreclr.dll",
            "BepInEx/plugins/BaseButler.dll",
            "BepInEx/plugins/BaseButler.HomeAccess.dll",
            "BepInEx/plugins/BaseButler.ContainerNavigation.dll",
            "BepInEx/plugins/BaseButler.ContainerInlineEditor.dll",
            "BepInEx/plugins/BaseButler.NativeCompatibility.dll",
            "BepInEx/plugins/SurvivalLog.Mods.dll"
        )
        foreach ($r in $required) {
            if (-not ($names -contains ($prefix + $r))) { $fail.Add("缺少必需文件: $prefix$r") }
        }
        if (-not ($names | Where-Object { $_ -like "${prefix}BepInEx/unity-libs/*.zip" })) {
            $fail.Add("缺少 Unity 基础库（${prefix}BepInEx/unity-libs/*.zip），无法生成 interop")
        }
        foreach ($cmd in @("Install.cmd", "Uninstall.cmd", "Check.cmd", "Launch.cmd")) {
            if (-not ($names -contains $cmd)) { $fail.Add("缺少入口脚本: $cmd") }
        }
        if (-not ($names -contains "先读我.txt")) { $warn.Add("根目录缺少 先读我.txt") }

        # manifest 与实体文件一致性
        $mPath = Join-Path $pkgDir.FullName "manifest.json"
        $m = Get-Content $mPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $t = @($m.Payload) | Where-Object { $_.Target -eq "BepInEx\plugins\SurvivalLog.Mods.dll" }
        if (-not $t) { $fail.Add("manifest 的 Payload 未登记插件") }
        $pf = @($m.PackageFiles) | Where-Object { $_.Path -eq "payload/BepInEx/plugins/SurvivalLog.Mods.dll" }
        if (-not $pf) { $fail.Add("manifest 的 PackageFiles 未登记插件") }
        elseif ($pf.SHA256.ToUpperInvariant() -ne $dllHash) { $fail.Add("manifest 记录的 DLL 哈希与实际不符") }

        foreach ($item in $m.PackageFiles) {
            $abs = Join-Path $pkgDir.FullName ($item.Path -replace '/', '\')
            if (-not (Test-Path -LiteralPath $abs)) { $fail.Add("manifest 登记的文件不存在: $($item.Path)"); continue }
            if ((Get-Sha256 $abs) -ne $item.SHA256.ToUpperInvariant()) { $fail.Add("文件哈希与 manifest 不符: $($item.Path)") }
        }

        if (Test-Path (Join-Path $stage "先读我.txt")) {
            $txt = [System.IO.File]::ReadAllText((Join-Path $stage "先读我.txt"))
            if ($txt -notmatch "SurvivalLog\.Mods") { $warn.Add("先读我.txt 未写入插件说明") }
        }
    }
}
finally { $zip.Dispose() }

if ($warn.Count) { foreach ($w in $warn) { Write-Warning $w } }
if ($fail.Count) {
    Write-Host ""
    Write-Host "自检失败，请勿发布：" -ForegroundColor Red
    foreach ($f in $fail) { Write-Host "  · $f" -ForegroundColor Red }
    exit 1
}

# ---- 报告 -------------------------------------------------------------
$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
$hash = Get-Sha256 $zipPath
$fileCount = (Get-ChildItem $stage -Recurse -File | Measure-Object).Count

Write-Host ""
Write-Host "打包完成: $zipPath" -ForegroundColor Green
Write-Host "模式: $(if ($Lite) { '增量包（需已装 HomeButler）' } else { '完整安装包（含 BepInEx 基底）' })" -ForegroundColor Green
Write-Host "大小: $sizeMb MB   文件数: $fileCount" -ForegroundColor Green
Write-Host "SHA256: $hash" -ForegroundColor Green
Write-Host "自检: 通过" -ForegroundColor Green
if (-not $Lite) {
    Write-Host ""
    Write-Host "用户安装方式：解压到游戏根目录 → 双击 Install.cmd" -ForegroundColor Cyan
}

# 供 CI 读取（显式 UTF-8 无 BOM，避免 Windows PowerShell 5.1 写入 BOM 影响解析）
if ($env:GITHUB_OUTPUT) {
    $lines = @(
        "zip_path=$zipPath",
        "zip_name=$zipName",
        "zip_sha256=$hash",
        "version=$Version",
        "edition=$edition"
    ) -join "`n"
    [System.IO.File]::AppendAllText($env:GITHUB_OUTPUT, $lines + "`n", $utf8NoBom)
}
