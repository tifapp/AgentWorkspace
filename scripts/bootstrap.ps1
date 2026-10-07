$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$repo=Split-Path -Parent $PSScriptRoot
$tools=Join-Path $repo '.tools'
New-Item -ItemType Directory -Force -Path $tools | Out-Null
$installer=Join-Path $tools 'dotnet-install.ps1'
Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
& $installer -Version 10.0.401 -InstallDir (Join-Path $tools 'dotnet') -NoPath
if(!(Test-Path -LiteralPath (Join-Path $tools 'dotnet/dotnet.exe'))) { throw 'Local SDK installation failed.' }
Write-Output 'Local SDK installed. Next: scripts/build.ps1 -Test -Publish'
