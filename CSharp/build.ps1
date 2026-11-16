$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$sefmDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $sefmDotnet) { throw 'Install the .NET 10 SDK to rebuild. The supplied SEFM.exe runs without the SDK.' }
& $sefmDotnet.Source publish (Join-Path $PSScriptRoot 'src\SEFM.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $PSScriptRoot 'artifacts\publish')
if ($LASTEXITCODE -ne 0) { throw 'C# publishing failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'artifacts\publish\SEFM.exe') -Destination (Join-Path $PSScriptRoot 'SEFM.exe') -Force
Write-Output 'Built the standalone SEFM.exe. Run .\SEFM.exe self-test to verify it.'
