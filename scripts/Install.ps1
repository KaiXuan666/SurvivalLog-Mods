#Requires -Version 5.1
<#
构建 SurvivalLog.Butler 并安装到游戏目录的 BepInEx\plugins\。
游戏正在运行时会中止，避免覆盖正在使用的文件。
#>
[CmdletBinding()]
param(
    [string]$GameDir = "F:\platform\Steam\steamapps\common\Survival Log",
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$pluginsDir = Join-Path $GameDir "BepInEx\plugins"
if (-not (Test-Path $pluginsDir)) {
    throw "未找到插件目录：$pluginsDir。请确认游戏目录与 BepInEx 已就绪。"
}

$gameRunning = Get-Process -Name "SurvivalLog" -ErrorAction SilentlyContinue
if ($gameRunning) {
    throw "检测到游戏正在运行（PID: $($gameRunning.Id -join ', ')）。请先关闭游戏再安装。"
}

& (Join-Path $PSScriptRoot "Build.ps1") -GameDir $GameDir -Configuration $Configuration

$src = Join-Path $PSScriptRoot "..\src\SurvivalLog.Mods\bin\$Configuration\net6.0\SurvivalLog.Butler.dll"
if (-not (Test-Path $src)) { throw "未找到构建输出：$src" }

Copy-Item $src -Destination $pluginsDir -Force
Write-Host "已安装到: $(Join-Path $pluginsDir 'SurvivalLog.Butler.dll')" -ForegroundColor Green
Write-Host "请启动游戏，并检查 BepInEx\LogOutput.log 中的加载信息。" -ForegroundColor Yellow
