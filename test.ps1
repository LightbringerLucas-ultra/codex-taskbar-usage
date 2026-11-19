$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1') -Test
