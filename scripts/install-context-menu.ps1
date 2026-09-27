# 超级便签 - 资源管理器右键菜单注册脚本（当前用户，无需管理员）
# 用法：在 PowerShell 中执行  ./install-context-menu.ps1
# 卸载：./install-context-menu.ps1 -Uninstall

param(
    [switch]$Uninstall
)

$exe = Join-Path $PSScriptRoot "..\src\SuperNote.App\bin\Debug\net8.0-windows\SuperNote.exe"
$exe = [System.IO.Path]::GetFullPath($exe)

$keys = @(
    "HKCU:\Software\Classes\*\shell\SuperNote",
    "HKCU:\Software\Classes\Directory\shell\SuperNote"
)

if ($Uninstall) {
    foreach ($k in $keys) {
        if (Test-Path $k) { Remove-Item -Path $k -Recurse -Force }
    }
    Write-Host "已移除超级便签右键菜单。" -ForegroundColor Yellow
    exit 0
}

if (-not (Test-Path $exe)) {
    Write-Warning "未找到可执行文件：$exe"
    Write-Warning "请先运行 dotnet build，或修改本脚本中的路径。"
}

foreach ($k in $keys) {
    New-Item -Path $k -Force | Out-Null
    New-ItemProperty -Path $k -Name "(Default)" -Value "添加/查看超级便签" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $k -Name "Icon" -Value $exe -PropertyType String -Force | Out-Null

    $cmdKey = Join-Path $k "command"
    New-Item -Path $cmdKey -Force | Out-Null
    New-ItemProperty -Path $cmdKey -Name "(Default)" -Value "`"$exe`" --annotate `"%1`"" -PropertyType String -Force | Out-Null
}

Write-Host "已注册超级便签右键菜单（文件和文件夹）。" -ForegroundColor Green
Write-Host "可执行文件：$exe"
Write-Host "如移动了 exe，请重新运行本脚本。"
