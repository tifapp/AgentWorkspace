$ErrorActionPreference='Stop'
$app=Join-Path $PSScriptRoot 'AgentOS.exe'
if(!(Test-Path -LiteralPath $app)) { $app=Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/release/agent-os-retirement-win-x64/AgentOS.exe' }
if(!(Test-Path -LiteralPath $app)) { throw 'Build the portable release first: scripts/build.ps1 -Publish' }
if([Environment]::OSVersion.Version.Build -lt 19041) { throw 'agent-os requires Windows 10 build 19041 or later (x64).' }
$process=Start-Process -FilePath $app -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 3
if($process.HasExited -and $process.ExitCode -ne 0) { throw "agent-os exited with code $($process.ExitCode). Keep all files from the release folder together. Check %LOCALAPPDATA%\AgentOS\app-errors.log." }
