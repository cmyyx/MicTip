<#
.SYNOPSIS
    同时发布两个版本的 MicTip: 自包含 (~68MB) 和框架依赖 (~3-5MB)

.DESCRIPTION
    - self-contained: 内置 .NET 运行时, 目标机器无需安装任何依赖
    - framework-dependent: 体积小, 需目标机器已安装 .NET 8 桌面运行时

.PARAMETER SelfContainedOnly
    只发布自包含版本

.PARAMETER FrameworkDependentOnly
    只发布框架依赖版本

.EXAMPLE
    .\publish.ps1
    发布两个版本到 src\MicTip\bin\publish\

.EXAMPLE
    .\publish.ps1 -FrameworkDependentOnly
    只发布框架依赖版本
#>
param(
    [switch]$SelfContainedOnly,
    [switch]$FrameworkDependentOnly
)

$ErrorActionPreference = "Stop"
$project = "src\MicTip\MicTip.csproj"

function Publish-Version([string]$profileName, [string]$outputDir, [string]$label) {
    Write-Host "`n=== Publishing $label ===" -ForegroundColor Cyan
    dotnet publish $project -p:PublishProfile=$profileName --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to publish $label"
        exit 1
    }

    $publishDir = "src\MicTip\bin\publish\$outputDir"
    $exe = Join-Path $publishDir "MicTip.exe"
    if (Test-Path $exe) {
        $sizeMB = [math]::Round((Get-Item $exe).Length / 1MB, 2)
        Write-Host "  -> $exe ($sizeMB MB)" -ForegroundColor Green
    }
}

$publishBoth = -not $SelfContainedOnly -and -not $FrameworkDependentOnly

if ($publishBoth -or $SelfContainedOnly) {
    Publish-Version "SelfContained" "self-contained" "Self-Contained (no .NET runtime needed)"
}
if ($publishBoth -or $FrameworkDependentOnly) {
    Publish-Version "FrameworkDependent" "framework-dependent" "Framework-Dependent (requires .NET 8 Desktop Runtime)"
}

Write-Host "`nDone." -ForegroundColor Cyan
