$ErrorActionPreference='Stop'
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
foreach($name in 'package-signed.ps1','update-agentos.ps1'){
 $tokens=$null; $errors=$null
 [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repo ('scripts/'+$name)),[ref]$tokens,[ref]$errors) | Out-Null
 if($errors.Count){throw ($name+' has '+$errors.Count+' PowerShell syntax errors.')}
}
[xml]$manifest=Get-Content (Join-Path $repo 'packaging/AppxManifest.xml.in') -Raw
if($manifest.Package.Identity.Name -ne 'AgentOS.Desktop'){throw 'Packaging identity changed.'}
'Package scripts and manifest parse successfully.'
