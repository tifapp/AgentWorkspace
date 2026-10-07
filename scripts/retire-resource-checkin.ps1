param([switch]$Apply,[string]$CodexDirectory="$HOME/.codex",[string]$Session='agent-os-retirement-20261006',[string]$Key='replacement',
 [string]$Evidence='artifacts/retirement',[string]$ReceiptName='retirement-receipt.json')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$codexRoot=[IO.Path]::GetFullPath($CodexDirectory)
$skill=Join-Path $codexRoot 'skills/resource-checkin'
$registryTool=Join-Path $skill 'scripts/registry.mjs'
$registry=Join-Path $codexRoot 'active-work.md'
if($env:RESOURCE_CHECKIN_REGISTRY -and [IO.Path]::GetFullPath($env:RESOURCE_CHECKIN_REGISTRY) -ne $registry){throw 'A different legacy registry override is active. Cutover is refused.'}
$instructions=Join-Path $codexRoot 'AGENTS.md'
$evidenceRoot=[IO.Path]::GetFullPath((Join-Path $repo $Evidence))
$desktop=Get-Content (Join-Path $evidenceRoot 'desktop-final-acceptance/result.json') -Raw|ConvertFrom-Json
$tests=Get-Content (Join-Path $evidenceRoot 'tests-final-acceptance/results.json') -Raw|ConvertFrom-Json
$comparison=Get-Content (Join-Path $evidenceRoot 'comparison-final-acceptance/result.json') -Raw|ConvertFrom-Json
if([IO.Path]::GetFileName($ReceiptName) -ne $ReceiptName){throw 'Receipt name must be a filename.'}
if(!$desktop.passed -or @($tests.results|Where-Object {!$_.passed}).Count -or $tests.results.Count -lt 34 -or !$comparison.passed){throw 'Required replacement evidence has not passed.'}
if($tests.coreSha256 -ne $desktop.coreSha256 -or $comparison.coreSha256 -ne $desktop.coreSha256){throw 'Replacement evidence must refer to the same runtime binary.'}
if((Get-FileHash (Join-Path (Split-Path $desktop.app) 'AgentOS.Core.dll')).Hash -ne $desktop.coreSha256){throw 'The published runtime changed after desktop validation.'}
$entries=(& node $registryTool peek --full|ConvertFrom-Json).entries
if($LASTEXITCODE -ne 0){throw 'The legacy registry could not be inspected.'}
$raw=Get-Content -LiteralPath $registry -Raw
$match=[regex]::Match($raw,'(?s)<!-- resource-checkin:start -->\s*```json\s*(.*?)\s*```\s*<!-- resource-checkin:end -->')
if(!$match.Success){throw 'Legacy registry format cannot be established.'}
$stored=$match.Groups[1].Value|ConvertFrom-Json
if($stored.version -ne 2){throw 'Legacy registry schema cannot be established.'}
$entries=@($stored.entries)
$others=@($entries|Where-Object {$_.session -ne $Session -or $_.key -ne $Key})
if($others.Count){throw 'Legacy work is still active. Cutover is refused; no entry will be erased or expired to proceed.'}
if(!$Apply){[ordered]@{ready=$true;activeLegacyTasks=$entries.Count;coreSha256=$desktop.coreSha256}|ConvertTo-Json;return}
$archive=[IO.Path]::GetFullPath((Join-Path $codexRoot ('retired-skills/resource-checkin-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))))
if($skill -ne [IO.Path]::GetFullPath((Join-Path $codexRoot 'skills/resource-checkin')) -or !$archive.StartsWith($codexRoot+'\retired-skills\',[StringComparison]::OrdinalIgnoreCase)){throw 'Retirement paths are outside their explicit boundaries.'}
[IO.Directory]::CreateDirectory($archive)|Out-Null
Copy-Item -LiteralPath $instructions -Destination (Join-Path $archive 'AGENTS.before.md')
Copy-Item -LiteralPath $registry -Destination (Join-Path $archive 'active-work.before.md')
& node $registryTool checkout --session $Session --key $Key|Out-Null
if($LASTEXITCODE -ne 0){throw 'Could not hand off the implementation entry.'}
$lockPath=$registry+'.lock'; $lock=$null; $frozen=$false; $moved=$false; $protectedOriginal=$false
try {
 $lock=[IO.File]::Open($lockPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
 $bytes=[Text.Encoding]::UTF8.GetBytes(([ordered]@{pid=$PID;token=[Guid]::NewGuid().ToString();createdAt=[DateTimeOffset]::UtcNow.ToString('o')}|ConvertTo-Json -Compress))
 $lock.Write($bytes,0,$bytes.Length);$lock.Flush($true)
 $remaining=(& node $registryTool peek --full|ConvertFrom-Json).entries
 if($LASTEXITCODE -ne 0 -or @($remaining).Count){throw 'Legacy work arrived during handoff. Cutover stopped before changing the registry.'}
 Copy-Item -LiteralPath $registry -Destination (Join-Path $archive 'active-work.drained.md')
 # Unsupported schema makes already-loaded legacy clients fail closed after this lock is released.
 $tombstone=@'
# Retired resource-checkin registry

Coordination moved to agent-os. Read ~/.codex/AGENTS.md and the retirement receipt.
This unsupported version deliberately prevents old clients from admitting new work.

<!-- resource-checkin:start -->
```json
{"version":99,"entries":[]}
```
<!-- resource-checkin:end -->
'@
 $frozen=$true;[IO.File]::WriteAllText($registry,$tombstone,[Text.UTF8Encoding]::new($false))
 try {Move-Item -LiteralPath $skill -Destination (Join-Path $archive 'skill');$moved=$true}
 catch [System.IO.IOException] {
  # Host-protected skill directories may forbid deletion. Preserve those ACLs;
  # archive a read-only copy and retire execution through policy and schema.
  if(!$_.Exception.Message.Contains('denied')){throw}
  Copy-Item -LiteralPath $skill -Destination (Join-Path $archive 'skill') -Recurse
  $protectedOriginal=$true
 }
 $replacement=@'
# Automatic Codex coordination

resource-checkin is retired. Do not check in, renew, declare resources, or check out.

For supported development mutations, launch work through agent-os Desktop or its CLI
`run <project> <PowerShell-validation-command> <task>`. Codex is the sole supported host.
The runtime derives ownership, isolates private commands, revalidates shared publication,
owns process trees and loopback previews, and records actual validation and decisions.

Read the project's README and docs/guarantees.md for the supported surface. Managed
PowerShell, scoped private Git and local publication are supported. Arbitrary native SDKs,
remote services, deployments, non-Git tasks and unmanaged tools are not automatically
mediated. Do not claim those operations are protected or silently bypass a failed gate.
Read-only investigation needs no coordination ritual. Keep authorized independent work
moving; resolve unsupported mutations from existing delegation and documented policy.

This one-time retirement and installation maintenance was explicitly authorized by the
user. The archive and receipt are in ~/.codex/retired-skills. No live legacy work was erased.
'@
 [IO.File]::WriteAllText($instructions,$replacement,[Text.UTF8Encoding]::new($false))
 Add-Content -LiteralPath $instructions -Value ("`nInstalled CLI: `""+(Join-Path $repo 'artifacts/release/agent-os-retirement-win-x64/cli/AgentOS.Cli.exe')+"`".`nSetup and guarantees: `""+(Join-Path $repo 'README.md')+"`".") -Encoding UTF8
 $receipt=[ordered]@{schema=1;retiredAt=[DateTimeOffset]::UtcNow.ToString('o');archive=$archive;protectedOriginalRetained=$protectedOriginal;coreSha256=$desktop.coreSha256;legacyEntriesAtCutover=0;legacySchemaAfterCutover=99;instructionsSha256=(Get-FileHash $instructions).Hash;scope='Supported managed Codex workflows for this Windows user. Unmanaged and unsupported operations are not mediated.';evidence=$evidenceRoot}
 $receipt|ConvertTo-Json -Depth 6|Set-Content (Join-Path $archive 'receipt.json') -Encoding UTF8
 $receipt|ConvertTo-Json -Depth 6|Set-Content (Join-Path $evidenceRoot $ReceiptName) -Encoding UTF8
 $receipt|ConvertTo-Json -Depth 6
} catch {
 if($moved){Move-Item -LiteralPath (Join-Path $archive 'skill') -Destination $skill}
 if($frozen){Copy-Item -LiteralPath (Join-Path $archive 'active-work.drained.md') -Destination $registry -Force;Copy-Item -LiteralPath (Join-Path $archive 'AGENTS.before.md') -Destination $instructions -Force}
 throw
} finally {if($lock){$lock.Dispose();Remove-Item -LiteralPath $lockPath}}
