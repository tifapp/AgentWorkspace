param([switch]$Publish, [switch]$Test)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repo
$local=Join-Path $repo '.tools/dotnet/dotnet.exe'
$sdk=if(Test-Path -LiteralPath $local) { $local } else { 'dotnet' }
& $sdk --version
if($LASTEXITCODE -ne 0) { throw 'A .NET 10 SDK is required. Run scripts/bootstrap.ps1.' }
& $sdk build src/AgentOS.App/AgentOS.App.csproj -c Release -v minimal
if($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
& $sdk build src/AgentOS.Cli/AgentOS.Cli.csproj -c Release -v minimal
if($LASTEXITCODE -ne 0) { throw 'Runtime CLI build failed.' }
if($Test) {
 & $sdk run --project tests/AgentOS.Tests -c Release -- artifacts/verification/tests
 if($LASTEXITCODE -ne 0) { throw 'Runtime integration checks failed.' }
}
if($Publish) {
 & $sdk publish src/AgentOS.App/AgentOS.App.csproj -c Release -o artifacts/release/agent-os-retirement-win-x64 -v minimal
 if($LASTEXITCODE -ne 0) { throw 'Desktop publication failed.' }
 & $sdk publish src/AgentOS.Cli/AgentOS.Cli.csproj -c Release -r win-x64 --self-contained true -o artifacts/release/agent-os-retirement-win-x64/cli -v minimal
 if($LASTEXITCODE -ne 0) { throw 'CLI publication failed.' }
 foreach($required in 'App.xbf','AgentOS.pri','AgentOS.exe','AgentOS.dll','AgentOS.Core.dll','hostfxr.dll','Microsoft.ui.xaml.dll','Microsoft.UI.Xaml.Controls.pri') {
  $publishedFile=Join-Path 'artifacts/release/agent-os-retirement-win-x64' $required
  if(!(Test-Path -LiteralPath $publishedFile)) { throw "Published application resource is missing: $required" }
  $builtFile=Join-Path 'src/AgentOS.App/bin/Release/net10.0-windows10.0.19041.0/win-x64' $required
  if((Get-FileHash -LiteralPath $publishedFile).Hash -ne (Get-FileHash -LiteralPath $builtFile).Hash) { throw "Published resource differs from the build: $required" }
 }
 Copy-Item -LiteralPath README.md -Destination artifacts/release/agent-os-retirement-win-x64/README.md -Force
 Copy-Item -LiteralPath docs/guarantees.md -Destination artifacts/release/agent-os-retirement-win-x64/GUARANTEES.md -Force
 New-Item -ItemType Directory -Force -Path artifacts/release/agent-os-retirement-win-x64/docs | Out-Null
 Copy-Item -Path docs/*.md -Destination artifacts/release/agent-os-retirement-win-x64/docs -Force
 Copy-Item -LiteralPath scripts/run-app.ps1 -Destination artifacts/release/agent-os-retirement-win-x64/Run-AgentOS.ps1 -Force
 Compress-Archive -Path artifacts/release/agent-os-retirement-win-x64/* -DestinationPath artifacts/agent-os-retirement-win-x64.zip -Force
 Write-Output "Portable app: $repo\artifacts\release\agent-os-retirement-win-x64\AgentOS.exe"
}
& "$PSScriptRoot/source-manifest.ps1"
