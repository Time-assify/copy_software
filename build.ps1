# 复制工具 - 一键编译脚本
# 由 编译.bat 调用。中文内容放在这里（UTF-8 带 BOM），PowerShell 能正确识别，
# 交给 csc.exe 的中文文件名也会按系统 ANSI 代码页正确传递。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host "============================================"
Write-Host "  重新编译 复制工具"
Write-Host "============================================"
Write-Host ""

$csc = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:SystemRoot\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) {
    Write-Host "[错误] 找不到 C# 编译器 csc.exe" -ForegroundColor Red
    Write-Host "       需要 .NET Framework 4.x（Windows 10/11 自带）"
    exit 1
}
Write-Host "编译器: $csc"
Write-Host ""

$refsWin = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll')
$refsCon = @('/r:System.dll', '/r:System.Core.dll')

$jobs = @(
    @{ Out = '复制工具.exe';           Kind = 'winexe'; Refs = $refsWin; Src = @('src\Program.cs', 'src\MainForm.cs', 'src\UiTheme.cs', 'src\RobocopyEngine.cs'); Res = 'src\wallpaper.jpg,B_CopyTool.wallpaper.jpg' },
    @{ Out = '复制工具-透明背景.exe';  Kind = 'winexe'; Refs = $refsWin; Src = @('src\Program.cs', 'src\MainForm.cs', 'src\UiTheme.cs', 'src\RobocopyEngine.cs'); Res = 'src\wallpaper.jpg,B_CopyTool.wallpaper.jpg'; Define = 'BACKDROP' },
    @{ Out = '引擎测试.exe';           Kind = 'exe';    Refs = $refsCon; Src = @('src\SelfTest.cs', 'src\RobocopyEngine.cs', 'src\InstallerEngine.cs') },
    @{ Out = '界面预览.exe';           Kind = 'exe';    Refs = $refsWin; Main = 'CopyTool.UiPreview'; Src = @('src\UiPreview.cs', 'src\MainForm.cs', 'src\UiTheme.cs', 'src\RobocopyEngine.cs', 'src\SetupWizard.cs', 'src\InstallerEngine.cs'); Res = 'src\wallpaper.jpg,B_CopyTool.wallpaper.jpg' },
    @{ Out = '安装向导.exe';           Kind = 'winexe'; Refs = $refsWin; Src = @('src\SetupWizard.cs', 'src\InstallerEngine.cs', 'src\UiTheme.cs'); Manifest = 'src\setup.manifest' },
    @{ Out = '卸载复制工具.exe';       Kind = 'winexe'; Refs = $refsWin; Src = @('src\Uninstaller.cs', 'src\InstallerEngine.cs', 'src\UiTheme.cs'); Manifest = 'src\setup.manifest' }
)

$n = 0
foreach ($j in $jobs) {
    $n++
    Write-Host ("[{0}/{1}] 编译 {2}" -f $n, $jobs.Count, $j.Out)

    $missing = @($j.Src | Where-Object { -not (Test-Path $_) })
    if ($missing.Count -gt 0) {
        Write-Host ("       缺少源文件: " + ($missing -join ', ')) -ForegroundColor Red
        exit 1
    }

    $cscArgs = @('/nologo', "/target:$($j.Kind)", '/codepage:65001', "/out:$($j.Out)") + $j.Refs + $j.Src
    # 所有 exe 统一嵌图标；安装/卸载程序带"需要管理员"清单（双击即弹 UAC）
    $icon = Join-Path $root 'src\app.ico'
    if (Test-Path $icon) { $cscArgs += ('/win32icon:"' + $icon + '"') }
    if ($j.Manifest) { $cscArgs += ('/win32manifest:"' + (Join-Path $root $j.Manifest) + '"') }
    if ($j.Main) { $cscArgs += ("/main:" + $j.Main) }
    if ($j.Res) { $cscArgs += ("/res:" + $j.Res) }
    if ($j.Define) { $cscArgs += ("/define:" + $j.Define) }
    & $csc @cscArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "       [失败] 编译出错，退出码 $LASTEXITCODE" -ForegroundColor Red
        exit 1
    }
    $size = (Get-Item $j.Out).Length
    Write-Host ("      成功  ({0:N0} 字节)" -f $size) -ForegroundColor Green
    Write-Host ""
}

Write-Host "============================================"
Write-Host "  编译完成"
Write-Host "============================================"
Write-Host "  复制工具.exe           双击运行（右侧立绘卡版式）"
Write-Host "  复制工具-透明背景.exe  双击运行（壁纸铺满窗口版式）"
Write-Host "  引擎测试.exe           命令行运行，验证引擎功能"
Write-Host "  界面预览.exe           离屏渲染界面，自检布局 + 像素配色"
Write-Host ""
Write-Host "建议接着跑一遍自测：" -ForegroundColor Cyan
Write-Host "  .\引擎测试.exe"
