#Requires -Version 5.1
<#
构建 SurvivalLog.Mods。
自动查找 .NET 6 SDK：优先 PATH 中的 dotnet，其次常见安装位置，最后按目录搜索。
#>
[CmdletBinding()]
param(
    [string]$GameDir = "F:\platform\Steam\steamapps\common\Survival Log",
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "..\src\SurvivalLog.Mods\SurvivalLog.Mods.csproj"

function Find-Dotnet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $known = @(
        "F:\DevTools\dotnet\dotnet.exe",
        "C:\Program Files\dotnet\dotnet.exe",
        "${env:LOCALAPPDATA}\Microsoft\dotnet\dotnet.exe"
    )
    foreach ($p in $known) {
        if (Test-Path $p) {
            $env:DOTNET_ROOT = Split-Path -Parent $p
            return $p
        }
    }

    foreach ($root in @("C:\", "E:\", "F:\")) {
        $hit = Get-ChildItem -Path $root -Filter dotnet.exe -Recurse -Depth 3 -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -notmatch "\\sdk\\" } |
               Select-Object -First 1
        if ($hit) {
            $env:DOTNET_ROOT = $hit.DirectoryName
            return $hit.FullName
        }
    }

    throw "未找到 .NET 6 SDK，请先安装：https://aka.ms/dotnet/download"
}

if (-not (Test-Path $GameDir)) {
    throw "游戏目录不存在：$GameDir。请用 -GameDir 指定正确路径。"
}

$dotnet = Find-Dotnet
Write-Host "使用 SDK: $dotnet" -ForegroundColor Cyan
Write-Host "游戏目录: $GameDir" -ForegroundColor Cyan

& $dotnet build $project -c $Configuration -p:GameDir="$GameDir"
if ($LASTEXITCODE -ne 0) { throw "构建失败（exit=$LASTEXITCODE）。" }

$dll = Join-Path (Split-Path -Parent $project) "bin\$Configuration\net6.0\SurvivalLog.Mods.dll"
if (-not (Test-Path $dll)) { throw "未找到构建输出：$dll" }

Write-Host "构建完成: $dll" -ForegroundColor Green
