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

function Test-DotnetSdk {
    param([string]$Exe)
    if (-not (Test-Path $Exe)) { return $false }
    # Windows PowerShell 5.1 会把原生命令的 stderr 当成错误记录，这里临时放开
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $sdks = & $Exe --list-sdks 2>&1
        if ($LASTEXITCODE -ne 0 -or -not $sdks) { return $false }
        $lines = @($sdks | Where-Object { $_ -is [string] -and $_ -match '^\d+\.\d+' })
        return ($lines.Count -gt 0)
    }
    finally { $ErrorActionPreference = $previous }
}

function Find-Dotnet {
    $candidates = New-Object System.Collections.Generic.List[string]

    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates.Add($cmd.Source) }

    foreach ($p in @(
        "F:\DevTools\dotnet\dotnet.exe",
        "C:\Program Files\dotnet\dotnet.exe",
        "${env:LOCALAPPDATA}\Microsoft\dotnet\dotnet.exe",
        "C:\Program Files (x86)\dotnet\dotnet.exe"
    )) { $candidates.Add($p) }

    foreach ($root in @("C:\", "E:\", "F:\")) {
        Get-ChildItem -Path $root -Filter dotnet.exe -Recurse -Depth 3 -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch "\\sdk\\|\\shared\\" } |
            ForEach-Object { $candidates.Add($_.FullName) }
    }

    foreach ($c in ($candidates | Select-Object -Unique)) {
        if (Test-DotnetSdk -Exe $c) {
            $env:DOTNET_ROOT = Split-Path -Parent $c
            return $c
        }
    }

    throw "未找到可用的 .NET SDK（需要能构建 net6.0），请先安装：https://aka.ms/dotnet/download"
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
