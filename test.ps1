$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'C# compiler not found.' }
$tempDir = Join-Path $PSScriptRoot 'work\tests'
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
$testExe = Join-Path $tempDir 'UsageTests.exe'
& $compiler /nologo /target:exe /platform:anycpu /codepage:65001 /main:UsageTests /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:Microsoft.CSharp.dll "/out:$testExe" (Join-Path $PSScriptRoot 'src\CodexTaskbarUsage.cs') (Join-Path $PSScriptRoot 'tests\UsageTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
