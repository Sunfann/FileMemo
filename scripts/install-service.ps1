#requires -Version 5.1
<#
.SYNOPSIS
  将 文笺 FileMemo 注册为 Windows 后台服务（需求 3.15 / 7.P2「后台服务」）。

.DESCRIPTION
  使用 FileMemo.exe 的 --service 无界面模式：
    - 大规模文件追踪（USN Journal）
    - 网络盘 / NAS 监控
    - 开机自动索引（含图片向量索引，若已启用）

  以管理员身份运行本脚本。默认用 sc.exe 注册；若检测到 NSSM，
  可改用 NSSM 以获得更完善的服务包装。

.PARAMETER ExePath
  FileMemo.exe 的完整路径。默认为脚本上一级 src\FileMemo.App\bin 下的常见输出位置。

.PARAMETER ServiceName
  服务名，默认 FileMemoBackground。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1 -ExePath "C:\Apps\FileMemo\FileMemo.exe"
#>
param(
    [string]$ExePath = "",
    [string]$ServiceName = "FileMemoBackground",
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "请以管理员身份运行本脚本（右键 → 以管理员身份运行 PowerShell）。"
    }
}

Assert-Admin

if ($Uninstall) {
    Write-Host "停止并删除服务 $ServiceName ..." -ForegroundColor Yellow
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        sc.exe delete $ServiceName | Out-Null
        Write-Host "已删除服务 $ServiceName。" -ForegroundColor Green
    } else {
        Write-Host "未找到服务 $ServiceName。" -ForegroundColor DarkGray
    }
    return
}

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $candidates = @(
        (Join-Path $PSScriptRoot "..\src\FileMemo.App\bin\Release\net8.0-windows10.0.19041.0\FileMemo.exe"),
        (Join-Path $PSScriptRoot "..\src\FileMemo.App\bin\Debug\net8.0-windows10.0.19041.0\FileMemo.exe")
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $ExePath = (Resolve-Path $c).Path; break }
    }
}

if ([string]::IsNullOrWhiteSpace($ExePath) -or -not (Test-Path $ExePath)) {
    throw "未找到 FileMemo.exe，请用 -ExePath 指定完整路径。"
}

Write-Host "使用可执行文件：$ExePath" -ForegroundColor Cyan

# 若已在运行，先停止旧服务
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "服务已存在，先停止 ..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

$nssm = Get-Command nssm.exe -ErrorAction SilentlyContinue
if ($nssm) {
    Write-Host "检测到 NSSM，使用 NSSM 注册服务 ..." -ForegroundColor Cyan
    & nssm.exe install $ServiceName $ExePath "--service"
    & nssm.exe set $ServiceName AppDirectory (Split-Path $ExePath)
    & nssm.exe set $ServiceName Start SERVICE_AUTO_START
    & nssm.exe set $ServiceName AppStdout (Join-Path $env:APPDATA "SuperNote\service-out.log")
    & nssm.exe set $ServiceName AppStderr (Join-Path $env:APPDATA "SuperNote\service-err.log")
    & nssm.exe start $ServiceName
} else {
    Write-Host "未检测到 NSSM，使用 sc.exe 注册（注意：普通 GUI 程序作为服务的稳定性有限，建议安装 NSSM） ..." -ForegroundColor Yellow
    sc.exe create $ServiceName binPath= "`"$ExePath`" --service" start= auto DisplayName= "文笺 FileMemo 后台服务" | Out-Null
    sc.exe description $ServiceName "文笺 FileMemo 后台追踪 / 索引服务（USN Journal + 网络盘监控 + 开机索引）" | Out-Null
    Start-Service -Name $ServiceName
}

Write-Host "服务 $ServiceName 已注册并启动。" -ForegroundColor Green
Write-Host "查看状态： Get-Service $ServiceName" -ForegroundColor DarkGray
Write-Host "卸载：     powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1 -Uninstall" -ForegroundColor DarkGray
