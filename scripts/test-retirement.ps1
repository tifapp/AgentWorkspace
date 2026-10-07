param([string]$Output='artifacts/retirement/migration-fixture')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$root=[IO.Path]::GetFullPath((Join-Path $repo $Output))
if(!$root.StartsWith($repo+'\artifacts\',[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture must stay within artifacts.'}
if(Test-Path -LiteralPath $root){throw 'Use a fresh fixture output.'}
[IO.Directory]::CreateDirectory((Join-Path $root 'skills'))|Out-Null
Copy-Item -LiteralPath "$HOME/.codex/skills/resource-checkin" -Destination (Join-Path $root 'skills/resource-checkin') -Recurse
[IO.File]::WriteAllText((Join-Path $root 'AGENTS.md'),'Original fixture instructions')
$cli=Join-Path $root 'skills/resource-checkin/scripts/registry.mjs'
$old=$env:RESOURCE_CHECKIN_REGISTRY
try {
 $env:RESOURCE_CHECKIN_REGISTRY=Join-Path $root 'active-work.md'
 & node $cli checkin --agent fixture --task cutover --session fixture-own --key install --write id:fixture-own|Out-Null
 if($LASTEXITCODE -ne 0){throw 'Fixture registration failed.'}
 & node $cli checkin --agent fixture --task other --session fixture-other --key other --write id:fixture-other|Out-Null
 if($LASTEXITCODE -ne 0){throw 'Other registration failed.'}
 $before=(Get-FileHash $env:RESOURCE_CHECKIN_REGISTRY).Hash
 $refused=$false
 try { & "$PSScriptRoot/retire-resource-checkin.ps1" -CodexDirectory $root -Session fixture-own -Key install -ReceiptName fixture-receipt.json } catch {if($_.Exception.Message -like 'Legacy work is still active*'){$refused=$true}else{throw}}
 if(!$refused -or (Get-FileHash $env:RESOURCE_CHECKIN_REGISTRY).Hash -ne $before){throw 'Active legacy work was not preserved.'}
 & node $cli checkout --session fixture-other --key other|Out-Null
 if($LASTEXITCODE -ne 0){throw 'Other checkout failed.'}
 & "$PSScriptRoot/retire-resource-checkin.ps1" -Apply -CodexDirectory $root -Session fixture-own -Key install -ReceiptName fixture-receipt.json|Out-Null
 $receipt=Get-Content (Join-Path $repo 'artifacts/retirement/fixture-receipt.json') -Raw|ConvertFrom-Json
 $frozen=(Get-FileHash $env:RESOURCE_CHECKIN_REGISTRY).Hash
 $savedPreference=$ErrorActionPreference;$ErrorActionPreference='Continue'
 & node (Join-Path $receipt.archive 'skill/scripts/registry.mjs') checkin --agent fixture --task late --session late --key late --write id:late 2> (Join-Path $root 'legacy-refusal.txt')|Out-Null
 $exit=$LASTEXITCODE;$ErrorActionPreference=$savedPreference
 if($exit -eq 0 -or (Get-FileHash $env:RESOURCE_CHECKIN_REGISTRY).Hash -ne $frozen -or (Test-Path -LiteralPath (Join-Path $root 'skills/resource-checkin'))){throw 'Legacy client was admitted after retirement.'}
 [ordered]@{passed=$true;activeWorkRefused=$true;activeRegistryPreserved=$true;archive=$receipt.archive;lateLegacyExit=$exit;lateRegistryUnchanged=$true;coreSha256=$receipt.coreSha256}|ConvertTo-Json|Set-Content (Join-Path $root 'result.json') -Encoding UTF8
 Get-Content (Join-Path $root 'result.json')
} finally {$env:RESOURCE_CHECKIN_REGISTRY=$old}
