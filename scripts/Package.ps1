#Requires -Version 5.1
<#
生成发布用的 zip。有两种模式：

  完整安装包（默认）
    以 HomeButler 官方离线整合包为基底，把本插件并入它的 payload 与 manifest，
    产出「解压到游戏根目录 + 运行 Install.cmd」即可用的一体化安装包。
    基底包含 BepInEx 6 (IL2CPP)、Doorstop、.NET 运行时与 Unity 基础库——
    这些是插件运行的必需条件，单独一个 DLL 在干净游戏上不会被加载。

  增量包（-Lite）
    只含 BepInEx\plugins\SurvivalLog.Butler.dll，供已装好 HomeButler 的用户升级。

  完整包还会把上游的游戏版本 / Unity 版本硬校验降级为提示（见 Disable-VersionChecks），
  因为游戏小版本更新后 mod 通常仍可用。需要保留上游行为时加 -KeepVersionChecks。

  完整包额外启用卸载自清理（见 Enable-SelfCleanup）：运行 Uninstall.cmd 撤销安装后，
  会把解压出来的整个包目录（含所有 .cmd）一并删除。

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
    [switch]$Lite,
    [switch]$KeepVersionChecks
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\SurvivalLog.Mods\SurvivalLog.Butler.csproj"
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

function Set-ZipUtf8Flag([string]$ZipPath) {
    # .NET 的 ZipArchive 用 UTF-8 写条目名，但**不会**设置通用标志位的 bit 11。
    # 没这个标志，解压端按 ZIP 规范默认用 CP437 解释文件名，
    # 于是「先读我.txt」「安装说明.txt」在资源管理器 / unzip / 7-Zip 里全变乱码。
    #
    # 关键：不能在整个文件里盲目搜索 PK 签名——压缩数据流里可能恰好出现同样的字节
    # （payload 里的 unity-libs/*.zip 本身就内嵌了完整 zip，几乎必然命中），
    # 改到那些字节就是静默破坏数据、解压时报 bad CRC。必须按真实结构跳读：
    # 本地文件头按「自身长度 + 声明的压缩后长度 + 文件名/扩展区长度」精确跳到下一个。
    # 中央目录位于结尾，从 EOCD 里读偏移，逐条按固定 46 字节头 + 变长字段递进。
    $bytes = [System.IO.File]::ReadAllBytes($ZipPath)
    $patched = 0

    # --- 1. 本地文件头：从偏移 0 开始顺序跳读 ---
    $i = 0
    while ($i + 30 -le $bytes.Length) {
        if (-not ($bytes[$i] -eq 0x50 -and $bytes[$i + 1] -eq 0x4B -and $bytes[$i + 2] -eq 0x03 -and $bytes[$i + 3] -eq 0x04)) { break }

        $flag = [int]$bytes[$i + 6] -bor ([int]$bytes[$i + 7] -shl 8)
        if (($flag -band 0x0800) -eq 0) {
            $new = $flag -bor 0x0800
            $bytes[$i + 6] = [byte]($new -band 0xFF)
            $bytes[$i + 7] = [byte](($new -shr 8) -band 0xFF)
            $patched++
        }

        $compSize = [BitConverter]::ToUInt32($bytes, $i + 18)
        $nameLen = [BitConverter]::ToUInt16($bytes, $i + 26)
        $extraLen = [BitConverter]::ToUInt16($bytes, $i + 28)
        $dataStart = $i + 30 + $nameLen + $extraLen
        if ($compSize -eq 0xFFFFFFFF -or $dataStart -gt $bytes.Length) { break }  # ZIP64 或结构异常，到此为止
        $i = $dataStart + $compSize
    }

    # --- 2. 中央目录项：从 EOCD 记录的偏移开始 ---
    $eocd = -1
    for ($k = $bytes.Length - 22; $k -ge 0 -and $k -ge $bytes.Length - 65557; $k--) {
        if ($bytes[$k] -eq 0x50 -and $bytes[$k + 1] -eq 0x4B -and $bytes[$k + 2] -eq 0x05 -and $bytes[$k + 3] -eq 0x06) { $eocd = $k; break }
    }
    if ($eocd -lt 0) { throw 'zip 缺少 EOCD，无法补写 UTF-8 标志位。' }

    $cdCount = [BitConverter]::ToUInt16($bytes, $eocd + 10)
    $j = [int][BitConverter]::ToUInt32($bytes, $eocd + 16)
    for ($n = 0; $n -lt $cdCount; $n++) {
        if ($j + 46 -gt $bytes.Length) { throw "中央目录第 $n 条越界，zip 结构异常。" }
        if (-not ($bytes[$j] -eq 0x50 -and $bytes[$j + 1] -eq 0x4B -and $bytes[$j + 2] -eq 0x01 -and $bytes[$j + 3] -eq 0x02)) {
            throw "中央目录第 $n 条签名不符，zip 结构异常。"
        }
        $flag = [int]$bytes[$j + 8] -bor ([int]$bytes[$j + 9] -shl 8)
        if (($flag -band 0x0800) -eq 0) {
            $new = $flag -bor 0x0800
            $bytes[$j + 8] = [byte]($new -band 0xFF)
            $bytes[$j + 9] = [byte](($new -shr 8) -band 0xFF)
            $patched++
        }
        $nameLen = [BitConverter]::ToUInt16($bytes, $j + 28)
        $extraLen = [BitConverter]::ToUInt16($bytes, $j + 30)
        $commentLen = [BitConverter]::ToUInt16($bytes, $j + 32)
        $j += 46 + $nameLen + $extraLen + $commentLen
    }

    [System.IO.File]::WriteAllBytes($ZipPath, $bytes)
    return $patched
}

function Test-ZipUtf8Flag([string]$ZipPath) {
    # 打包自检用：中央目录里每个条目都必须带 UTF-8 标志，否则中文名在解压端会乱码。
    $bytes = [System.IO.File]::ReadAllBytes($ZipPath)
    $eocd = -1
    for ($k = $bytes.Length - 22; $k -ge 0 -and $k -ge $bytes.Length - 65557; $k--) {
        if ($bytes[$k] -eq 0x50 -and $bytes[$k + 1] -eq 0x4B -and $bytes[$k + 2] -eq 0x05 -and $bytes[$k + 3] -eq 0x06) { $eocd = $k; break }
    }
    if ($eocd -lt 0) { return -1 }

    $cdCount = [BitConverter]::ToUInt16($bytes, $eocd + 10)
    $j = [int][BitConverter]::ToUInt32($bytes, $eocd + 16)
    $missing = 0
    for ($n = 0; $n -lt $cdCount; $n++) {
        if ($j + 46 -gt $bytes.Length) { return -1 }
        $flag = [int]$bytes[$j + 8] -bor ([int]$bytes[$j + 9] -shl 8)
        if (($flag -band 0x0800) -eq 0) { $missing++ }
        $nameLen = [BitConverter]::ToUInt16($bytes, $j + 28)
        $extraLen = [BitConverter]::ToUInt16($bytes, $j + 30)
        $commentLen = [BitConverter]::ToUInt16($bytes, $j + 32)
        $j += 46 + $nameLen + $extraLen + $commentLen
    }
    return $missing
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

function Apply-TextPatches {
    # 只替换最小片段；任一片段匹配次数不为 1 就中止，避免上游改版后静默失效。
    # 返回被改动的文件名列表，并同步 manifest 的 SHA-256（否则 Setup.ps1 会报 Package damaged）。
    param(
        [string]$PkgDir,
        $Manifest,
        [object[]]$Patches,
        [string]$Label = '补丁'
    )

    $touched = New-Object System.Collections.Generic.List[string]

    # 按文件分组：同一文件上的多个补丁必须在同一份文本上顺序替换。
    # 之前是「每条补丁都重新 ReadAllText」，看似安全，实则把上一条补丁的改动又读回来了，
    # 于是后一条基于旧文本定位、写回时覆盖掉前一条——多次补丁互相踩踏、全部错位。
    # 这里手动分桶而不是 Group-Object：$Patches 的元素是 hashtable，
    # 在 PowerShell 5.1 下 Group-Object -Property File 取不到键，会把它们并成一组、Name 为空。
    $files = New-Object System.Collections.Generic.List[string]
    $buckets = @{}
    foreach ($p in $Patches) {
        $f = [string]$p['File']
        if ([string]::IsNullOrWhiteSpace($f)) { throw "${Label}配置错误：有一条补丁没有写 File。" }
        if (-not $buckets.ContainsKey($f)) {
            $buckets[$f] = New-Object System.Collections.Generic.List[object]
            $files.Add($f)
        }
        $buckets[$f].Add($p)
    }

    foreach ($file in $files) {
        $path = Join-Path $PkgDir $file
        if (-not (Test-Path -LiteralPath $path)) { throw "${Label}失败：找不到 $file" }

        $bytes = [System.IO.File]::ReadAllBytes($path)
        $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
        $raw = [System.IO.File]::ReadAllText($path)

        # 本脚本自身是 LF，上游文件是 CRLF。\r\n -> \n 会改变文本长度，
        # 所以不在原文上按绝对位置替换，而是全程在归一化文本上做替换，
        # 最后一次还原成原来的换行风格写回：既不丢位置，也不会把 CRLF 文件改成 LF。
        $crlf = $raw.Contains("`r`n")
        $text = $raw -replace "`r`n", "`n"

        foreach ($p in $buckets[$file]) {
            $oldN = ([string]$p['Old']) -replace "`r`n", "`n"
            $hits = ([regex]::Matches($text, [regex]::Escape($oldN))).Count
            if ($hits -ne 1) {
                throw "${Label}失配：$file 中目标片段出现 $hits 次（期望 1）。上游可能已改版，请检查后再发布。"
            }
            $text = $text.Replace($oldN, (([string]$p['New']) -replace "`r`n", "`n"))
        }

        if ($crlf) { $text = $text -replace "`n", "`r`n" }
        [System.IO.File]::WriteAllText($path, $text, [System.Text.UTF8Encoding]::new($hasBom))
        $touched.Add($file)
    }

    foreach ($file in $touched) {
        $entry = @($Manifest.PackageFiles) | Where-Object { $_.Path -eq $file }
        if (-not $entry) { throw "manifest 未登记 $file，无法更新补丁后的哈希。" }
        $entry.SHA256 = Get-Sha256 (Join-Path $PkgDir $file)
    }

    return $touched
}

function Enable-SelfCleanup([string]$PkgDir, $Manifest) {
    # 卸载后自清理：撤销安装的同时，把解压出来的整个包目录删掉（含本脚本与所有 .cmd）。
    #
    # 清理逻辑放在独立的 SelfCleanup.ps1 里，Setup.ps1 只插入一行调用：
    # 一是不用把大段代码塞进单引号字符串（转义极难维护），
    # 二是删除清单、失败处理等逻辑都能单独阅读与测试。
    # 清单本身用 manifest 的 CleanupFiles 字段（见 Add-SelfCleanupManifest），不复用
    # PackageFiles——后者路径相对包目录且有哈希校验，混入别的路径会让安装器报 Package damaged。
    $patches = @(
        @{
            File = 'Setup.ps1'
            # 只在 Uninstall 分支打一个锚点：回滚成功后再调用随包分发的清理脚本。
            # 这段文本在上游 Setup.ps1 中只出现一次，且不涉及大括号，不会像 "</}" 那样误命中。
            Old  = '        Write-Host ''Uninstalled. Pre-existing files restored; unrelated files and saves untouched.''
        exit 0'
            New  = '        Write-Host ''Uninstalled. Pre-existing files restored; unrelated files and saves untouched.''
        # 自定义：回滚成功后删除整个解压包目录（含 Uninstall.cmd 自身）。
        # 回滚抛错时走 catch，不会执行到这里，包与 .state 备份都还在。
        $cleanupScript = Join-Path $packageRoot ''SelfCleanup.ps1''
        if(Test-Path -LiteralPath $cleanupScript){ & $cleanupScript -GameRoot $gameRoot -PackageRoot $packageRoot }
        else { Write-Warning ''[mods] SelfCleanup.ps1 missing; package folder left in place.'' }
        exit 0'
        }
    )

    return (Apply-TextPatches -PkgDir $PkgDir -Manifest $Manifest -Patches $patches -Label '自清理补丁')
}

function Add-SelfCleanupManifest([string]$PkgDir, $Manifest, [string]$StageRoot) {
    # 自清理的删除清单存在独立字段 CleanupFiles 里，路径相对于「解压后的根目录」。
    # 不复用 PackageFiles：那个字段的路径相对于包目录，且 Setup.ps1 启动时会逐条校验
    # 哈希，混入包目录以外的路径会让安装器直接报 Package damaged。
    # 注意：包目录自身的 .state 是备份与状态目录，不列入，否则会把备份删一半。
    $pkgLeaf = Split-Path $PkgDir -Leaf
    $list = New-Object System.Collections.Generic.List[object]
    $seen = @{}

    foreach ($root in @($StageRoot, $PkgDir)) {
        foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force) {
            $rel = $file.FullName.Substring($StageRoot.Length).TrimStart('\').Replace('\', '/')
            if ($rel -match '^' + [regex]::Escape($pkgLeaf) + '/\.state/') { continue }
            if ($seen.ContainsKey($rel)) { continue }
            $seen[$rel] = $true
            $list.Add([pscustomobject]@{ Path = $rel; SHA256 = Get-Sha256 $file.FullName })
        }
    }
    $Manifest | Add-Member -NotePropertyName 'CleanupFiles' -NotePropertyValue @($list.ToArray()) -Force
    return $list.Count
}

function Relax-UninstallPageChecks([string]$PkgDir, $Manifest) {
    # 卸载时上游要求「每个受管文件的内容仍等于安装时写下的哈希」，否则拒绝回滚。
    # 但容器 UI / 制作台 UI 是本插件在**游戏运行期**改写这些页面的：只要玩过一次游戏，
    # 页面哈希就必然与安装记录不同，用户会看到
    #   File changed after installation; refusing to overwrite: ...BackpackUI.html
    # 然后永远卸不掉。这里把「受管的游戏页面在运行期被改写」视为正常，仍按 .state 备份
    # 还原成原版；其余文件维持原有的严格校验，避免掩盖真正的损坏或人为修改。
    #
    # 与 Disable-VersionChecks 分开：这条关系到卸载能否走通，不应该被 -KeepVersionChecks 关掉。
    $patches = @(
        @{
            File = 'Setup.ps1'
            Old  = @'
function RestoreState($state) {
    $configRestores=@{}
    $preserveConfigs=@{}
'@
            New  = @'
function RestoreState($state) {
    $configRestores=@{}
    $preserveConfigs=@{}
    # 运行期被 mod 改写过的游戏页面：卸载时按备份还原，不视为文件损坏。
    $runtimePages=@{}
'@
        },
        @{
            File = 'Setup.ps1'
            Old  = @'
            } elseif($r.Path -like 'BepInEx\config\*.cfg') {
                $preserveConfigs[$r.Path]=$true
            } else {throw "File changed after installation; refusing to overwrite: $($r.Path)"}
'@
            New  = @'
            } elseif($r.Path -like 'BepInEx\config\*.cfg') {
                $preserveConfigs[$r.Path]=$true
            } elseif($r.Path -like '*StreamingAssets/WebUI/UI/*.html' -or $r.Path -like '*StreamingAssets\WebUI\UI\*.html') {
                $runtimePages[$r.Path]=$true
            } else {throw "File changed after installation; refusing to overwrite: $($r.Path)"}
'@
        },
        @{
            File = 'Setup.ps1'
            Old  = @'
        if($r.Before) {
            $backup=SafePath $packageRoot $r.Backup
            if((Hash $backup) -ne $r.Before){throw "Backup missing or damaged: $($r.Path)"}
        }
    }
    for($ri=@($state.Files).Count-1; $ri -ge 0; $ri--) {
'@
            New  = @'
        if($r.Before) {
            $backup=SafePath $packageRoot $r.Backup
            if((Hash $backup) -ne $r.Before){throw "Backup missing or damaged: $($r.Path)"}
        }
    }
    if($runtimePages.Count -gt 0) {
        Write-Host ("[mods] Restoring {0} game page(s) that mods rewrote while playing: {1}" -f $runtimePages.Count, (($runtimePages.Keys | Sort-Object) -join ', ')) -ForegroundColor DarkCyan
    }
    for($ri=@($state.Files).Count-1; $ri -ge 0; $ri--) {
'@
        }
    )

    return (Apply-TextPatches -PkgDir $PkgDir -Manifest $Manifest -Patches $patches -Label '卸载页面补丁')
}

function Disable-VersionChecks([string]$PkgDir, $Manifest) {
    # 上游把游戏版本 / Unity 版本当作安装门槛：Unity 版本不符直接 throw，
    # Launch.ps1 的构建指纹不符则进入安全模式（等于禁用 mod）。
    # 实测游戏更新后 mod 往往仍可用，因此把「阻断」降级为「提示」。
    $patches = @(
        @{
            File = 'Setup.ps1'
            Old  = 'if(!(Test-Path -LiteralPath $versionPath)){throw ''Game version file missing.''}'
            New  = 'if(!(Test-Path -LiteralPath $versionPath)){Write-Warning ''[mods] Game version file missing; continuing without version check.''}'
        },
        @{
            File = 'Setup.ps1'
            Old  = 'if($gameVersion -ne $manifest.GameVersion){Write-Warning "Untested game version $gameVersion; checking layout structure. Native API compatibility is not guaranteed."}'
            New  = 'if($gameVersion -ne $manifest.GameVersion){Write-Host "[mods] Game $gameVersion; package targets $($manifest.GameVersion). Continuing anyway."}'
        },
        @{
            File = 'Setup.ps1'
            Old  = 'if(!$unity.StartsWith(''6000.2.7.'')){throw ''Unity engine changed. This offline runtime supports 6000.2.7 only.''}'
            # "$unity" 保证读不到版本信息时是空串而不是 null，避免 null 方法调用直接崩掉
            New  = 'if(!("$unity".StartsWith(''6000.2.7.''))){Write-Warning "[mods] Unity $unity differs from tested 6000.2.7; continuing. If the game misbehaves, run SafeMode.cmd."}'
        },
        @{
            File = 'Launch.ps1'
            Old  = 'foreach($f in $manifest.LaunchFingerprints){if((Hash (SafePath $f.Path)) -ne $f.SHA256){$supported=$false}}'
            New  = 'Write-Host ''[mods] Game build fingerprint check disabled; relying on install state instead.'''
        },
        # 上游提示玩家「必须用 Launch.cmd 启动」。本包装好后 Steam 直接启动、双击
        # SurvivalLog.exe 都能加载（doorstop 已启用），所以改掉这句以免误导。
        @{
            File = 'Setup.ps1'
            Old  = 'Write-Host ''Installed successfully. Start the game through Launch.cmd. First launch generates interop locally using the bundled Unity archive; no component download needed.'''
            New  = 'Write-Host ''[mods] Installed. Launch the game normally (Steam or SurvivalLog.exe); Launch.cmd is only needed after a game update. First launch generates interop locally using the bundled Unity archive; no component download needed.'''
        }
    )

    return (Apply-TextPatches -PkgDir $PkgDir -Manifest $Manifest -Patches $patches -Label '版本校验补丁')
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
$dll = Join-Path $repoRoot "src\SurvivalLog.Mods\bin\$Configuration\net6.0\SurvivalLog.Butler.dll"

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
Survival Butler $Version —— 安装说明
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
       ...\Survival Log\BepInEx\plugins\SurvivalLog.Butler.dll
  3. 双击 Install.cmd，确认显示安装成功。

  Install.cmd 会写入 Doorstop 与 BepInEx 配置、注入页面脚本。
  首次启动游戏时 BepInEx 需要生成 IL2CPP 接口，可能耗时数分钟；
  本次启动通常还不会加载插件，再次启动后才生效。

从旧版升级（已经装过本插件的旧版本）
  按这个顺序做，否则会报 "Installation changed" 而装不上：

    1. 关闭游戏。
    2. 运行 Uninstall.cmd，卸载旧版本。
    3. 解压本包，覆盖到游戏根目录。
    4. 运行 Install.cmd。

  注意：不要跳过第 2 步直接解压新包。安装器按哈希记录判断「这个文件是不是安装时
  写下的」，而解压会覆盖掉已注入脚本的游戏页面（BackpackUI.html 等），安装器会
  认为安装被人为改过，从而拒绝继续。

  若已经直接解压了、并且 Install.cmd 报 "Installation changed"：
  重新运行一次 Uninstall.cmd（它会按备份恢复原文件），再走上面的 1~4。

从旧版升级（装过分开的两个旧插件）
  若之前装过分开的两个插件，先删除下面两个文件，否则会重复 patch：
       BepInEx\plugins\SurvivalLog.CharacterEnhancement.dll
       BepInEx\plugins\SurvivalLog.ContainerPicker.dll

  若之前已装过 HomeButler 官方包，直接解压本包并再次运行 Install.cmd 即可，
  安装器会跳过内容相同的文件。

启动方式
  装完之后照常启动游戏即可：Steam 里点开始、双击 SurvivalLog.exe 都能加载插件，
  不需要每次都走 Launch.cmd。

  HomeButler 的 Launch.cmd 是给「游戏更新后页面结构与备份对不上」这类情况用的
  安全启动开关：它会在开游戏前核对安装状态，必要时先退回原版页面再启动。
  所以游戏本体更新过、或容器界面出现异常时，仍建议用 Launch.cmd 启动一次。

  · 不用 Launch.cmd 直接启动时，安全模式检查同样生效：若安装状态不完整，
    游戏会以关掉 mod 的方式启动，而不是带着半套改动进游戏。
  · 若某次用 Launch.cmd 进过安全模式，游戏目录里的 doorstop_config.ini 会被改成
    enabled = false，此后直接启动不会加载任何 mod。这时用 Launch.cmd 启动一次
    （或运行 RestoreMods.cmd）即可恢复。

功能
  · 人物增强：移动速度与探索暴露度上限倍率可调，在主界面右侧栏的模组面板里
    可随时开关，改动立即生效并写入 BepInEx\config\local.survivallog.butler.cfg。
  · 模组面板：主界面（标题菜单）右侧栏底部的齿轮按钮，点开即可调整本插件参数。
  · 容器增强：背包容器下拉选择、拖放目标、自动归类、制作台配方右键。

排查
  用 ViewLog.cmd 打开 BepInEx\LogOutput.log，搜索 "Survival Butler"。
  未检测到 HomeButler 页面标记时，容器功能会跳过并在日志中说明，人物增强仍生效。

卸载
  运行 Uninstall.cmd 撤销安装。它按安装记录恢复原文件，不会删除存档与个人配置。

  撤销成功后，本包会删除整个解压出来的目录（含 Uninstall.cmd、其他 .cmd 与
  HomeButler-2.0.0-Package）。也就是说：装一次 → 用一阵 → 卸载，游戏目录回到
  装之前的样子，不留任何本包的文件。
  · 删除发生在回滚成功之后；回滚失败时整个包目录保留，便于排查后重试。
  · 若个别文件被占用或被安全软件拦截，会提示 "Package folder kept" 并列出残留文件，
    其余文件已经删除；关掉占用程序后手动删除剩余文件即可。
  · 想保留脚本与备份供以后对照，请在运行 Uninstall.cmd 之前把整个包目录复制到别处。

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
Survival Butler $Version —— 增量包安装说明
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
  · 人物增强：移动速度与探索暴露度上限倍率可调，在主界面右侧栏的模组面板里
    可随时开关，改动立即生效并写入 BepInEx\config\local.survivallog.butler.cfg。
  · 模组面板：主界面（标题菜单）右侧栏底部的齿轮按钮，点开即可调整本插件参数。
  · 容器增强：背包容器下拉选择、拖放目标、自动归类、制作台配方右键。

  启动方式同上：Steam 直接启动即可，不需要每次走 Launch.cmd。

排查
  查看 BepInEx\LogOutput.log，搜索 "Survival Butler"。

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
    $relSource = "payload/BepInEx/plugins/SurvivalLog.Butler.dll"
    $relTarget = "BepInEx\plugins\SurvivalLog.Butler.dll"

    # --- 移除上游的版本硬校验（需在写回 manifest 之前完成，以便同步哈希） ---
    $patchedFiles = @()
    if (-not $KeepVersionChecks) {
        $patchedFiles = Disable-VersionChecks -PkgDir $pkgDir.FullName -Manifest $manifest
        Write-Host "已移除版本硬校验：$($patchedFiles -join '、')" -ForegroundColor Cyan
    }

    # --- 卸载时容忍「运行期被改写的游戏页面」：不受 -KeepVersionChecks 影响 ---
    $relaxed = Relax-UninstallPageChecks -PkgDir $pkgDir.FullName -Manifest $manifest
    Write-Host "已放宽卸载检查：运行期改写过的游戏页面按备份还原（$($relaxed -join '、')）" -ForegroundColor Cyan

    $manifest.Payload = @(@($manifest.Payload) | Where-Object { $_.Target -ne $relTarget }) +
        [pscustomobject]@{ Target = $relTarget; Source = $relSource }

    $manifest.PackageFiles = @(@($manifest.PackageFiles) | Where-Object { $_.Path -ne $relSource }) +
        [pscustomobject]@{ Path = $relSource; SHA256 = $dllHash }

    # --- 升级白名单：允许覆盖「旧版本装下的同一个插件」---
    # 上游 Setup.ps1 对 payload 里的文件做了冲突保护：只有目标文件的哈希出现在
    # manifest.UpgradeHashes[目标路径] 里，才允许覆盖，否则报
    # "Existing file conflicts with this bundle" 并整体放弃安装。
    # 于是不做这一步的话，任何「装过旧版 → 装新版」的用户都会装不上。
    # 这里把旧版本发布过的哈希累积进白名单（含本次导入的上一版哈希）。
    $upgradeTarget = $relTarget
    $allowed = New-Object System.Collections.Generic.List[string]

    $existingEntry = $manifest.UpgradeHashes.PSObject.Properties[$upgradeTarget]
    if ($existingEntry -and $existingEntry.Value) {
        foreach ($h in @($existingEntry.Value)) { if ($h) { $allowed.Add([string]$h) } }
    }

    # 本仓库自己发布过的版本：随包一起改，每次打包由发布者把上一版的哈希填进
    # scripts/SurvivalLog.Butler.known-hashes.txt（一行一个 SHA-256）。
    $knownPath = Join-Path $repoRoot "scripts\SurvivalLog.Butler.known-hashes.txt"
    if (Test-Path -LiteralPath $knownPath) {
        foreach ($line in (Get-Content -LiteralPath $knownPath -Encoding ASCII)) {
            $h = $line.Trim().ToUpperInvariant()
            if ($h -match '^[0-9A-F]{64}$' -and -not $allowed.Contains($h)) { $allowed.Add($h) }
        }
    }

    # 当前游戏目录里已装的那个 DLL：本次产出的若与它不同，说明用户在升级，必须放行。
    if ($GameDir) {
        $installedPlugins = Join-Path $GameDir "BepInEx\plugins\SurvivalLog.Butler.dll"
        if (Test-Path -LiteralPath $installedPlugins) {
            $installedHash = Get-Sha256 -Path $installedPlugins
            if ($installedHash -and $installedHash -ne $dllHash -and -not $allowed.Contains($installedHash)) {
                $allowed.Add($installedHash)
                Write-Host "升级白名单：已放行游戏目录中的旧版插件 $($installedHash.Substring(0,12))…" -ForegroundColor Cyan
            }
        }
    }

    # 本次产出自身的哈希也要在表里：重装同一版本时安装器会跳过（哈希相同），
    # 但保留它能让「先卸载后重装」这类路径不至于因缺项报错。
    if (-not $allowed.Contains($dllHash)) { $allowed.Add($dllHash) }

    $hashTable = [ordered]@{}
    if ($manifest.UpgradeHashes) {
        foreach ($p in $manifest.UpgradeHashes.PSObject.Properties) { $hashTable[$p.Name] = @($p.Value) }
    }
    $hashTable[$upgradeTarget] = @($allowed)
    $manifest | Add-Member -NotePropertyName UpgradeHashes -NotePropertyValue ([pscustomobject]$hashTable) -Force

    [System.IO.File]::WriteAllText($mPath, ($manifest | ConvertTo-Json -Depth 20), $utf8NoBom)

    # --- 卸载后自清理：随包分发 SelfCleanup.ps1，并让 Setup.ps1 在回滚成功后调用它 ---
    $cleanupScript = Join-Path $repoRoot "scripts\SelfCleanup.ps1"
    if (-not (Test-Path -LiteralPath $cleanupScript)) { throw "缺少卸载自清理脚本：$cleanupScript" }
    Copy-Item $cleanupScript -Destination (Join-Path $pkgDir.FullName "SelfCleanup.ps1") -Force

    $cleanupFiles = Enable-SelfCleanup -PkgDir $pkgDir.FullName -Manifest $manifest
    $patchedFiles = @(@($patchedFiles) + @($cleanupFiles) | Where-Object { $_ } | Select-Object -Unique)

    # --- 追加说明到「先读我.txt」（保持原有 BOM） ---
    $readmePath = Join-Path $stage "先读我.txt"
    if (Test-Path $readmePath) {
        $versionNote = if ($KeepVersionChecks) {
            "版本校验：保留上游原样（游戏版本不符会警告，Unity 版本不符会中止安装）。"
        }
        else {
            @"
版本校验：本包已移除上游的游戏版本 / Unity 版本硬校验。
  · Setup.ps1：版本不符只提示，不再中止安装；
  · Launch.ps1：不再比对游戏构建指纹，不会因此自动进入安全模式。
  之所以这样做，是因为游戏小版本更新后 mod 经常仍可正常使用；版本号本身不足以
  判断兼容性。代价是失去了上游的自动保护：若游戏大版本更新导致界面结构或 IL2CPP
  接口变化，插件可能失效甚至报错——此时请运行 SafeMode.cmd 关闭 mod 对照排查，
  或删除本插件等待适配。
"@
        }

        $extra = @"

────────────────────────────────────────────────────────────────
本包已加入 Survival Butler $Version（人物增强 + 容器增强整合插件）
────────────────────────────────────────────────────────────────
安装步骤与上文「三步安装」完全相同，无需额外操作。
插件源文件：HomeButler-2.0.0-Package\payload\BepInEx\plugins\SurvivalLog.Butler.dll
安装后位置：BepInEx\plugins\SurvivalLog.Butler.dll
功能：移动速度 x3、探索暴露度上限 x5；背包容器下拉选择、拖放目标、
      自动归类、制作台配方右键。
排查：ViewLog.cmd 打开 BepInEx\LogOutput.log，搜索 "Survival Butler"。
卸载：运行 Uninstall.cmd 会一并撤销本插件，并删除整个解压包目录
      （含本说明、所有 .cmd 与 HomeButler-2.0.0-Package）。
      备份与回滚发生在删除之前；若回滚失败，包目录会保留，便于排查后重试。
许可：HomeButler-2.0.0-Package\licenses\ 与本包 licenses\ 目录。

$versionNote

被修改的上游文件：$($patchedFiles -join '、')（详见 MODS-PATCH.md，校验值已同步更新）
"@
        [System.IO.File]::AppendAllText($readmePath, $extra, $utf8Bom)
    }

    # --- 记录对上游脚本的改动，便于追溯 ---
    if ($patchedFiles.Count -gt 0) {
        $patchDoc = @"
# 对 HomeButler 离线包所做的改动

本包由 Survival Butler 的打包脚本（`scripts/Package.ps1`）在**解压官方离线包之后**生成，
仅对下列文件做了文本替换，其余文件与上游逐字节一致。

## 改动目的

1. 移除游戏版本与 Unity 版本的**硬校验**。理由：游戏小版本更新后 mod 通常仍可正常使用，
版本号本身不足以判断兼容性。上游把版本不符当作安装门槛，会让用户在可用的版本上装不了。

## 改动明细

| 文件 | 改动 |
| --- | --- |
| `Setup.ps1` | 版本文件缺失：`throw` → `Write-Warning` |
| `Setup.ps1` | 游戏版本不符：`Write-Warning` → 仅输出提示，不影响安装 |
| `Setup.ps1` | Unity 版本不符：`throw` → `Write-Warning` |
| `Launch.ps1` | 构建指纹比对：移除循环，不再因指纹不符自动进入安全模式 |
| `Setup.ps1` | `Uninstall` 分支：`RestoreState` 成功后调用随包分发的 `SelfCleanup.ps1` |
| — | 新增 `SelfCleanup.ps1`（本包独有文件，上游没有） |

被修改文件的 SHA-256 已同步写入 `manifest.json` 的 `PackageFiles`，因此上游安装器
的完整性校验仍能通过。

## 卸载自清理

运行 `Uninstall.cmd` 撤销安装后，解压出来的整个包目录会被删除，**包括 `Uninstall.cmd`
自己**、`Install.cmd`、`先读我.txt`、`安装说明.txt` 与 `HomeButler-2.0.0-Package` 目录，
游戏根目录只留下游戏本体的文件。

- 删除清单来自 `manifest.json` 的 `CleanupFiles` 字段（打包时生成，覆盖解压后根目录里的
  每个文件，并且额外登记 `manifest.json` 自身），**不递归删除包目录**：包目录位于游戏根目录内，
  一旦解压层级异常，递归删除会打到游戏本体。按清单删除只命中本包自己的文件。
- 路径相对于**解压根目录**，与 `PackageFiles`（相对包目录、带哈希校验）刻意分开，
  混用会让安装器启动时报 `Package damaged`。
- `HomeButler-2.0.0-Package\.state\`（备份与安装状态）不登记为待删文件，由清理脚本
  在所有文件删净后单独移除，避免把备份删一半。
- 删除发生在**回滚成功之后**。回滚抛错时 `Setup.ps1` 走 catch 分支，包目录与 `.state`
  备份都保留，可以排查后重跑 `Uninstall.cmd`。
- 杀软或文件占用可能导致个别文件删不掉。此时包目录保留，并列出失败条目提示手动删除；
  删除结果以「删完之后文件还在不在」为准，不会把已经删掉的文件误报为残留。

## 没有改动的东西

- `Setup.ps1` 的安装 / 卸载 / 状态恢复逻辑，`Payload` 驱动的文件复制、页面注入、
  备份与回滚，全部保持原样；
- `SafeMode.cmd` / `RestoreMods.cmd` 的安全模式机制保留，仍可手动使用；
- 页面结构校验（`AdaptiveLayout`、`PanelLayout`）与文件冲突检测保留。

## 使用了 `-KeepVersionChecks` 打包时

此文件不会出现，包内脚本与上游完全一致。

生成时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
"@
        [System.IO.File]::WriteAllText((Join-Path $pkgDir.FullName "MODS-PATCH.md"), $patchDoc, $utf8NoBom)
    }


    # --- release.json 标注整合来源 ---
    $relPath = Join-Path $stage "release.json"
    if (Test-Path $relPath) {
        $rel = Get-Content $relPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $rel | Add-Member -NotePropertyName "mods" -NotePropertyValue ([pscustomobject]@{
            name    = "Survival Butler"
            version = $Version
            source  = "https://github.com/KaiXuan666/SurvivalLog-Mods"
        }) -Force
        [System.IO.File]::WriteAllText($relPath, ($rel | ConvertTo-Json -Depth 10), $utf8NoBom)
    }
}

[System.IO.File]::WriteAllText((Join-Path $stage "安装说明.txt"), $installNotes, $utf8Bom)

# --- 自清理删除清单：必须在所有文件写完、打包之前生成，否则会漏登记 ---
if ($pkgDir) {
    $cleanupCount = Add-SelfCleanupManifest -PkgDir $pkgDir.FullName -Manifest $manifest -StageRoot $stage
    $mPath = Join-Path $pkgDir.FullName "manifest.json"
    [System.IO.File]::WriteAllText($mPath, ($manifest | ConvertTo-Json -Depth 20), $utf8NoBom)

    # manifest.json 自己也要删，但它的哈希只有写回后才能算：先写一次不含自己的清单，
    # 算出哈希后再把这一条追加进去写第二次。
    $selfRel = "$(Split-Path $pkgDir.FullName -Leaf)/manifest.json"
    $manifest.CleanupFiles = @($manifest.CleanupFiles) + [pscustomobject]@{ Path = $selfRel; SHA256 = Get-Sha256 $mPath }
    [System.IO.File]::WriteAllText($mPath, ($manifest | ConvertTo-Json -Depth 20), $utf8NoBom)

    Write-Host "卸载自清理已启用：CleanupFiles $($cleanupCount + 1) 条（含 manifest.json 自身）" -ForegroundColor Cyan
}

# ---- 打包 -------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$edition = if ($Lite) { "lite" } else { "full" }
$zipName = "Survival.Butler-$Version-$edition.zip"
$zipPath = Join-Path $OutputDir $zipName

# Compress-Archive 在 PowerShell 5.1 下写反斜杠条目名，这里用自建条目（见 New-ZipFromDirectory）
New-ZipFromDirectory -SourceDir $stage -ZipPath $zipPath

# 补上 UTF-8 标志位：.NET 写条目名用 UTF-8 但不设 bit 11，
# 缺少它时「先读我.txt」「安装说明.txt」在资源管理器 / unzip / 7-Zip 里会显示成乱码。
$utf8Flagged = Set-ZipUtf8Flag -ZipPath $zipPath
Write-Host "已为 $utf8Flagged 处条目补写 UTF-8 标志位" -ForegroundColor Cyan

# ---- 自检 -------------------------------------------------------------
$fail = New-Object System.Collections.Generic.List[string]
$warn = New-Object System.Collections.Generic.List[string]

Add-Type -AssemblyName System.IO.Compression
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entries = @($zip.Entries | Where-Object { $_.Name -ne "" })
    if ($entries.Count -eq 0) { $fail.Add("zip 为空") }

    # 中文条目名必须能被正确还原：只要有一份没打 UTF-8 标志，解压端就会按 CP437 读成乱码。
    $missingFlag = Test-ZipUtf8Flag -ZipPath $zipPath
    if ($missingFlag -gt 0) { $fail.Add("有 $missingFlag 个中央目录条目缺少 UTF-8 标志位（中文文件名会乱码）") }

    foreach ($e in $entries) {
        if ($e.FullName.Contains("\")) { $fail.Add("条目使用反斜杠: $($e.FullName)") }
        if ($e.FullName -match "(^|/)\.\.(/|$)") { $fail.Add("条目含路径穿越: $($e.FullName)") }
    }

    $names = @($entries | ForEach-Object { $_.FullName })

    if (-not ($names -contains "安装说明.txt")) { $fail.Add("缺少 安装说明.txt") }

    if ($Lite) {
        if (-not ($names -contains "BepInEx/plugins/SurvivalLog.Butler.dll")) { $fail.Add("缺少插件 DLL") }
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
            "BepInEx/plugins/SurvivalLog.Butler.dll"
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
        $t = @($m.Payload) | Where-Object { $_.Target -eq "BepInEx\plugins\SurvivalLog.Butler.dll" }
        if (-not $t) { $fail.Add("manifest 的 Payload 未登记插件") }
        $pf = @($m.PackageFiles) | Where-Object { $_.Path -eq "payload/BepInEx/plugins/SurvivalLog.Butler.dll" }
        if (-not $pf) { $fail.Add("manifest 的 PackageFiles 未登记插件") }
        elseif ($pf.SHA256.ToUpperInvariant() -ne $dllHash) { $fail.Add("manifest 记录的 DLL 哈希与实际不符") }

        # 升级白名单必须覆盖本次的 DLL，也要覆盖已知的旧版本，否则老用户装不上。
        $uh = $m.UpgradeHashes.PSObject.Properties["BepInEx\plugins\SurvivalLog.Butler.dll"]
        if (-not $uh -or -not @($uh.Value).Contains($dllHash)) {
            $fail.Add("manifest 的 UpgradeHashes 未包含本次 DLL 哈希（旧版本无法覆盖升级）")
        }
        $knownPath = Join-Path $repoRoot "scripts\SurvivalLog.Butler.known-hashes.txt"
        if (Test-Path -LiteralPath $knownPath) {
            foreach ($line in (Get-Content -LiteralPath $knownPath -Encoding ASCII)) {
                $h = $line.Trim().ToUpperInvariant()
                if ($h -match '^[0-9A-F]{64}$' -and -not @($uh.Value).Contains($h)) {
                    $fail.Add("manifest 的 UpgradeHashes 漏掉了已发布版本 $($h.Substring(0,12))…（升级会被判为文件冲突）")
                }
            }
        }

        foreach ($item in $m.PackageFiles) {
            $abs = Join-Path $pkgDir.FullName ($item.Path -replace '/', '\')
            if (-not (Test-Path -LiteralPath $abs)) { $fail.Add("manifest 登记的文件不存在: $($item.Path)"); continue }
            if ((Get-Sha256 $abs) -ne $item.SHA256.ToUpperInvariant()) { $fail.Add("文件哈希与 manifest 不符: $($item.Path)") }
        }

        if (Test-Path (Join-Path $stage "先读我.txt")) {
            $txt = [System.IO.File]::ReadAllText((Join-Path $stage "先读我.txt"))
            if ($txt -notmatch "Survival Butler") { $warn.Add("先读我.txt 未写入插件说明") }
        }

        # 版本校验补丁必须真的生效，不能因为替换失败而静默放过
        if (-not $KeepVersionChecks) {
            $setupText = [System.IO.File]::ReadAllText((Join-Path $pkgDir.FullName "Setup.ps1"))
            $launchText = [System.IO.File]::ReadAllText((Join-Path $pkgDir.FullName "Launch.ps1"))
            # 检测补丁后的标志文本，而不是原始 throw 片段——后者在补丁后的提示语里仍然出现
            if ($setupText -notmatch 'differs from tested 6000\.2\.7') { $fail.Add("补丁未生效：Setup.ps1 仍会因 Unity 版本中止安装") }
            if ($setupText -notmatch 'continuing without version check') { $fail.Add("补丁未生效：Setup.ps1 仍要求版本文件存在") }
            if ($launchText -match "LaunchFingerprints") { $fail.Add("补丁未生效：Launch.ps1 仍比对构建指纹") }
            if (-not (Test-Path (Join-Path $pkgDir.FullName "MODS-PATCH.md"))) { $fail.Add("缺少 MODS-PATCH.md（改动说明）") }
        }

        # 自清理补丁必须真的生效，且删除清单要覆盖解压后的每个文件
        $setupOut = [System.IO.File]::ReadAllText((Join-Path $pkgDir.FullName "Setup.ps1"))
        if ($setupOut -notmatch 'SelfCleanup\.ps1' -or $setupOut -notmatch '-GameRoot \$gameRoot') {
            $fail.Add("自清理补丁未生效：Uninstall 分支未调用 SelfCleanup.ps1")
        }
        if ($setupOut -notmatch 'SelfCleanup\.ps1') { $fail.Add("自清理补丁未生效：Setup.ps1 未引用 SelfCleanup.ps1") }
        if (-not (Test-Path (Join-Path $pkgDir.FullName "SelfCleanup.ps1"))) { $fail.Add("缺少 SelfCleanup.ps1") }

        # 卸载页面容错补丁：没有它，玩过游戏之后卸载会报 File changed after installation
        if ($setupOut -notmatch '\$runtimePages') { $fail.Add("卸载页面补丁未生效：Setup.ps1 仍会因页面被运行期改写而拒绝卸载") }
        if ($setupOut -notmatch 'StreamingAssets/WebUI/UI/\*\.html') { $fail.Add("卸载页面补丁未生效：Setup.ps1 没有识别游戏页面路径") }

        $cleanup = @($m.CleanupFiles)
        if ($cleanup.Count -eq 0) { $fail.Add("manifest 缺少 CleanupFiles（卸载后不会自清理）") }
        else {
            foreach ($c in $cleanup) {
                $abs = Join-Path $stage ($c.Path -replace '/', '\')
                if (-not (Test-Path -LiteralPath $abs)) { $fail.Add("CleanupFiles 条目不存在: $($c.Path)") }
            }
            # 解压后根目录里的每个文件都必须在清单里，否则会被留成垃圾
            foreach ($f in Get-ChildItem -LiteralPath $stage -File -Recurse -Force) {
                $rel = $f.FullName.Substring($stage.Length).TrimStart('\').Replace('\', '/')
                if (-not ($cleanup | Where-Object { $_.Path -eq $rel })) { $fail.Add("CleanupFiles 漏登记: $rel") }
            }
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
