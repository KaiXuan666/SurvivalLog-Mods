#Requires -Version 5.1
<#
卸载后自清理：删除整个解压包目录，让游戏目录回到装 mod 之前的样子。

由 Setup.ps1 的 Uninstall 分支在 RestoreState 成功之后调用，因此：
  · 回滚（还原游戏页面、配置、插件）已经完成；
  · 回滚抛错时不会走到这里，包目录与 .state 备份都还在，可以排查后重试。

删除范围来自 manifest.json 的 CleanupFiles（打包时生成，覆盖解压后根目录里的每个文件），
而不是「递归删除包目录」：包目录位于游戏根目录内，一旦 .state 位置异常或解压层级不对，
递归删除会打到游戏本体。按清单删除只会命中本包自己的文件。

用法（一般无需手动执行）：
  powershell -ExecutionPolicy Bypass -File SelfCleanup.ps1 -GameRoot "<游戏根目录>" -PackageRoot "<包目录>"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GameRoot,
    [Parameter(Mandatory = $true)][string]$PackageRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$gameRoot = [IO.Path]::GetFullPath($GameRoot).TrimEnd('\')
$pkgRoot = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')
$sep = [IO.Path]::DirectorySeparatorChar

# 包目录必须在游戏根目录之内，且不能等于根目录本身
if (-not $pkgRoot.StartsWith($gameRoot + $sep, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package folder is not inside the game folder; refusing to delete: $pkgRoot"
}

$manifestPath = Join-Path $pkgRoot 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'manifest.json not found; nothing to clean up.' }
$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json

$names = @($manifest.CleanupFiles | ForEach-Object { $_.Path })
if ($names.Count -eq 0) { throw 'manifest.json has no CleanupFiles; refusing to guess what to delete.' }

$failed = New-Object 'System.Collections.Generic.List[string]'
$deleted = 0

# ---- 先撤掉插件在运行期注入到主界面里的脚本 ----
# ButlerMenu 启动时把菜单脚本写进 MainUI.html。这个页面不在安装器记录的受管清单里
# （Setup.ps1 只管它自己打过标记的那 10 个容器页面），所以 RestoreState 不会碰它。
# 不在这里处理，卸载后页面上会留下一个点了没反应的按钮和一段无人接收的脚本。
$utf8NoBom = New-Object System.Text.UTF8Encoding -ArgumentList $false
$menuMarker = '<script id="survival-butler-menu-v1">'
$mainUi = Join-Path $gameRoot 'SurvivalLog_Data\StreamingAssets\WebUI\UI\MainUI\MainUI.html'

if (Test-Path -LiteralPath $mainUi) {
    try {
        $text = [IO.File]::ReadAllText($mainUi, [Text.Encoding]::UTF8)
        $start = $text.IndexOf($menuMarker, [StringComparison]::Ordinal)
        if ($start -ge 0) {
            $closeTag = '</script>'
            $end = $text.IndexOf($closeTag, $start, [StringComparison]::Ordinal)
            if ($end -lt 0) { throw 'Injected menu script is incomplete.' }
            $end += $closeTag.Length
            # 连注入时补上的换行一起去掉，尽量还原成注入前的样子。
            while ($start -gt 0 -and ($text[$start - 1] -eq "`n" -or $text[$start - 1] -eq "`r")) { $start-- }
            while ($end -lt $text.Length -and ($text[$end] -eq "`n" -or $text[$end] -eq "`r")) { $end++ }
            [IO.File]::WriteAllText($mainUi, ($text.Substring(0, $start) + $text.Substring($end)), $utf8NoBom)
            Write-Host 'Removed the mod menu script from the main menu page.'
        }
    }
    catch {
        $failed.Add("SurvivalLog_Data\StreamingAssets\WebUI\UI\MainUI\MainUI.html (menu script left in place)")
    }
}

# 插件自己留的页面备份目录不属于解压包，单独删。
# 这些目录在游戏跑起来之后才会出现，重装时会按页面哈希再生成，留着只会越积越多。
$ownBackupDirs = @(
    'BepInEx\Butler-menu-backups',        # ButlerMenu：主界面页面
    'BepInEx\ContainerPicker-backups'     # ContainerPicker：容器页面
)
foreach ($rel in $ownBackupDirs) {
    $dir = Join-Path $gameRoot $rel
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    try { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction Stop } catch { }
    if (Test-Path -LiteralPath $dir) { $failed.Add($rel) }
}

function Remove-One([string]$full) {
    # 以「删完之后文件还在不在」为准，而不是信任 Remove-Item 有没有抛异常：
    # 杀软 / 同步盘 / 文件占用插件可能在文件其实已删掉的情况下仍然报错，
    # 那种情况不该记成失败，否则会把已经干净的文件全列成残留、误导用户去手动删。
    try { Remove-Item -LiteralPath $full -Force -ErrorAction Stop }
    catch { }
    return -not (Test-Path -LiteralPath $full)
}

foreach ($name in $names) {
    $full = [IO.Path]::GetFullPath((Join-Path $gameRoot $name))
    # 双重保险：绝不操作包目录之外的东西（game 根下的其他 mod / 存档 / 游戏本体）
    if (-not $full.StartsWith($gameRoot + $sep, [StringComparison]::OrdinalIgnoreCase)) {
        $failed.Add("$name (outside game folder)")
        continue
    }
    if (-not (Test-Path -LiteralPath $full)) { continue }
    if (Remove-One $full) { $deleted++ } else { $failed.Add($name) }
}

# 自底向上删除变空的子目录（深层优先），最后删包目录本身。
$dirs = New-Object 'System.Collections.Generic.List[string]'
foreach ($name in $names) {
    $dir = Split-Path (Join-Path $gameRoot $name) -Parent
    while ($dir -and $dir.Length -gt $pkgRoot.Length -and $dir.StartsWith($pkgRoot + $sep, [StringComparison]::OrdinalIgnoreCase)) {
        if (-not $dirs.Contains($dir)) { $dirs.Add($dir) }
        $dir = Split-Path $dir -Parent
    }
}
foreach ($dir in ($dirs | Sort-Object -Property Length -Descending)) {
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    if (-not @(Get-ChildItem -LiteralPath $dir -Force)) {
        if (-not (Remove-One $dir)) { $failed.Add($dir) }
    }
}

# 包目录里的 .state（备份与安装状态）不登记在 CleanupFiles 里——它属于安装器，
# 由卸载流程自己处理；这里只在其余文件都删净、目录已空时才顺带删掉。
$stateDir = Join-Path $pkgRoot '.state'
if (Test-Path -LiteralPath $stateDir) {
    try { Remove-Item -LiteralPath $stateDir -Recurse -Force -ErrorAction Stop } catch { }
    if (Test-Path -LiteralPath $stateDir) { $failed.Add($stateDir) }
}

$remaining = @(Get-ChildItem -LiteralPath $pkgRoot -Force -ErrorAction SilentlyContinue)
if ($remaining.Count -eq 0) {
    if (-not (Remove-One $pkgRoot)) { $failed.Add($pkgRoot) }
}

if (Test-Path -LiteralPath $pkgRoot) {
    Write-Host "Removed $deleted file(s). Package folder kept because these could not be deleted:" -ForegroundColor Yellow
    foreach ($f in $failed) { Write-Host "  $f" -ForegroundColor Yellow }
    Write-Host "Close programs holding them and delete the folder manually." -ForegroundColor Yellow
    exit 0
}

Write-Host "Removed $deleted file(s); package folder deleted. Nothing left to clean up."
exit 0
