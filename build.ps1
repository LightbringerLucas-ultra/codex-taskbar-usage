$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '未找到 .NET Framework C# 编译器。' }
$outDir = Join-Path $PSScriptRoot 'app'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$source = Join-Path $PSScriptRoot 'src\CodexTaskbarUsage.cs'
$manifest = Join-Path $PSScriptRoot 'src\widget.manifest'
$output = Join-Path $outDir 'CodexTaskbarUsage.exe'
& $compiler /nologo /target:winexe /platform:anycpu /codepage:65001 "/win32manifest:$manifest" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:Microsoft.CSharp.dll "/out:$output" $source
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出代码 $LASTEXITCODE。" }
Write-Host "构建完成：$output"
