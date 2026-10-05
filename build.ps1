# build.ps1 —— 编译内存回收工具
# 使用 Windows 自带的 .NET Framework C# 编译器，无需安装 .NET SDK。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File build.ps1
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Clean

param(
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir  = Join-Path $root 'src'
$outDir  = Join-Path $root 'dist'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    # 回退到 32 位编译器
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw "找不到 C# 编译器 (csc.exe)。请确认已安装 .NET Framework 4.x。"
}

if ($Clean) {
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
    Write-Host "已清理输出目录" -ForegroundColor Yellow
}

if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$sources = Get-ChildItem -Path $srcDir -Filter '*.cs' | Select-Object -ExpandProperty FullName
if ($sources.Count -eq 0) { throw "在 $srcDir 中找不到任何 .cs 源文件" }

# 清单文件：嵌入 requireAdministrator，使双击时由 Windows 直接提权
$manifest = Join-Path $srcDir 'app.manifest'
if (-not (Test-Path $manifest)) { throw "找不到清单文件: $manifest" }

$exe = Join-Path $outDir 'MemReclaim.exe'

Write-Host "源文件 ($($sources.Count) 个):" -ForegroundColor Cyan
$sources | ForEach-Object { Write-Host "  $(Split-Path -Leaf $_)" }
Write-Host "清单: app.manifest (requireAdministrator)"
Write-Host ""

$args = @(
    '/nologo'
    '/target:winexe'        # 托盘程序，不要控制台窗口
    '/platform:anycpu'
    '/optimize+'
    "/win32manifest:$manifest"
    "/out:$exe"
    '/reference:System.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
    '/reference:System.Xml.dll'
) + $sources

Write-Host "编译中..." -ForegroundColor Cyan
& $csc $args
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }

$item = Get-Item $exe
Write-Host ""
Write-Host "编译成功" -ForegroundColor Green
Write-Host "  输出: $($item.FullName)"
Write-Host "  大小: $([math]::Round($item.Length/1KB,1)) KB"
Write-Host ""

# 同时生成一个控制台版本，便于查看 --selftest / --once 的输出。
# 注意用 asInvoker 清单：它常被计划任务调用，强制提权会挂住任务并丢失输出。
$exeConsole = Join-Path $outDir 'MemReclaimConsole.exe'
$consoleManifest = Join-Path $srcDir 'app-console.manifest'
$consoleArgs = @(
    '/nologo'
    '/target:exe'           # 控制台程序
    '/platform:anycpu'
    '/optimize+'
    "/win32manifest:$consoleManifest"
    "/out:$exeConsole"
    '/reference:System.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
    '/reference:System.Xml.dll'
) + $sources

& $csc $consoleArgs
if ($LASTEXITCODE -eq 0) {
    Write-Host "控制台版: $exeConsole（asInvoker，用于 --selftest / --once）" -ForegroundColor Green
}

Write-Host ""
Write-Host "下一步：" -ForegroundColor Cyan
Write-Host "  1. 以管理员身份运行 dist\MemReclaimConsole.exe --selftest   检测本机可用性"
Write-Host "  2. 以管理员身份运行 dist\MemReclaim.exe                    启动托盘程序"
