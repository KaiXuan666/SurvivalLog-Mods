#Requires -Version 5.1
<#
核对 HomeButler 前置依赖是否已正确安装。

HomeButler 为第三方作品，其授权未授予再分发权，因此本仓库不携带其源码或二进制文件。
请从上游获取并解压到游戏目录后，用本脚本比对版本与 SHA-256。

用法：
  .\scripts\Get-HomeButler.ps1
  .\scripts\Get-HomeButler.ps1 -GameDir "D:\Steam\steamapps\common\Survival Log"
#>
[CmdletBinding()]
param(
    [string]$GameDir = "F:\platform\Steam\steamapps\common\Survival Log"
)

$manifestPath = Join-Path $PSScriptRoot "..\third-party\HomeButler.json"
if (-not (Test-Path $manifestPath)) { throw "缺少依赖清单：$manifestPath" }

$manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
Write-Host "依赖: $($manifest.name) $($manifest.version)" -ForegroundColor Cyan
Write-Host "上游: $($manifest.upstream)" -ForegroundColor Cyan
Write-Host ""

$allOk = $true
foreach ($file in $manifest.files) {
    $target = Join-Path $GameDir $file.path
    if (-not (Test-Path $target)) {
        Write-Host "[缺失] $($file.path)" -ForegroundColor Red
        $allOk = $false
        continue
    }

    $actual = (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant()
    $expected = $file.sha256.ToLowerInvariant()
    $sizeOk = (Get-Item $target).Length -eq $file.size

    if ($actual -eq $expected -and $sizeOk) {
        Write-Host "[一致] $($file.path)" -ForegroundColor Green
    }
    elseif ($actual -eq $expected) {
        Write-Host "[一致] $($file.path)（大小与记录不同，可能是更新后的同版本重打包）" -ForegroundColor Yellow
    }
    else {
        Write-Host "[不符] $($file.path)" -ForegroundColor Red
        Write-Host "        记录: $expected"
        Write-Host "        实际: $actual"
        $allOk = $false
    }
}

if ($allOk) {
    Write-Host ""
    Write-Host "HomeButler 校验通过，可以启动游戏。" -ForegroundColor Green
}
else {
    Write-Host ""
    Write-Host "HomeButler 未就绪。请前往 $($manifest.upstream) 下载 $($manifest.packageTitle)，按包内文档安装到游戏目录后重新运行本脚本。" -ForegroundColor Yellow
}
