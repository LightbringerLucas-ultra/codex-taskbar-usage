param([switch]$Test)

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '未找到 .NET Framework C# 编译器。' }

$src = Join-Path $PSScriptRoot 'src'
$sources = @((Join-Path $src 'CodexTaskbarUsage.cs'), (Join-Path $src 'ApiCreditBalance.cs'), (Join-Path $src 'ClientProcesses.cs'))
$references = @(
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll',
    '/reference:Microsoft.CSharp.dll', "/reference:$framework\WPF\UIAutomationClient.dll",
    "/reference:$framework\WPF\UIAutomationTypes.dll", "/reference:$framework\WPF\WindowsBase.dll"
)

if ($Test) {
    $outDir = Join-Path $PSScriptRoot 'work\tests'
    $output = Join-Path $outDir 'UsageTests.exe'
    $target = @('/target:exe', '/main:UsageTests')
    $sources += (Join-Path $PSScriptRoot 'tests\UsageTests.cs')
} else {
    $outDir = Join-Path $PSScriptRoot 'app'
    $output = Join-Path $outDir 'CodexTaskbarUsage.exe'
    $target = @('/target:winexe')
}
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
& $compiler /nologo /platform:anycpu /codepage:65001 $target "/win32manifest:$src\widget.manifest" "/resource:$src\claude.png,Claude.png" "/resource:$src\codex.png,Codex.png" $references "/out:$output" $sources
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出代码 $LASTEXITCODE。" }
if ($Test) {
    & $output
    if ($LASTEXITCODE -ne 0) { throw '测试未通过。' }
} else {
    Write-Host "构建完成：$output"
}
