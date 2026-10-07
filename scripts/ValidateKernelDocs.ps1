[CmdletBinding()]
param(
    [string]$Root = $null,
    [switch]$RequireComplete,
    [switch]$SelfCheck
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Split-Path -Parent $PSScriptRoot }
$script:failures = New-Object System.Collections.Generic.List[string]
function Fail([string]$message) { $script:failures.Add($message) }
function Need($condition, [string]$message) { if (-not $condition) { Fail $message } }
function Properties($object) { if ($null -eq $object) { return @() }; return @($object.PSObject.Properties.Name) }
function ExactKeys($object, [string[]]$keys, [string]$where) {
    $actual = @(Properties $object)
    foreach ($k in $keys) { Need ($actual -ccontains $k) "$where missing $k" }
    foreach ($k in $actual) { Need ($keys -ccontains $k) "$where has unexpected $k" }
}
function ResolveRepoPath([string]$base, [string]$link, [string]$description) {
    if ([string]::IsNullOrWhiteSpace($link) -or $link -match '^(?:[a-z]+:|//|\\\\)' -or [IO.Path]::IsPathRooted($link)) {
        Fail "$description must use a local relative path: $link"; return $null
    }
    $target = ($link -split '[#?]',2)[0]
    if (!$target) { return $null }
    try { $full = [IO.Path]::GetFullPath((Join-Path $base $target)) }
    catch { Fail "$description invalid path $link"; return $null }
    $boundary = [IO.Path]::GetFullPath($Root).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)) {
        Fail "$description escapes root: $link"; return $null
    }
    $relative=$full.Substring($boundary.Length)
    $walk=$boundary.TrimEnd('\','/')
    foreach ($part in ($relative -split '[\\/]')) {
        if (!$part) { continue }
        $walk=Join-Path $walk $part
        if (Test-Path -LiteralPath $walk) {
            $item=Get-Item -LiteralPath $walk -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Fail "$description crosses reparse point: $link"; return $null }
        }
    }    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { Fail "$description broken: $link"; return $null }
    if ($link -match '#([^?]+)' -and [IO.Path]::GetExtension($full) -ieq '.md') {
        $fragment=[uri]::UnescapeDataString($Matches[1])
        $body=Get-Content -LiteralPath $full -Raw
        $anchors=@{}
        foreach ($heading in [regex]::Matches($body,'(?m)^#{1,6}\s+(.+?)\s*#*\s*$')) {
            $slug=$heading.Groups[1].Value.ToLowerInvariant()
            $slug=[regex]::Replace($slug,'[^\p{L}\p{N}\s_-]','')
            $slug=[regex]::Replace($slug,'\s+','-').Trim('-')
            $anchors[$slug]=$true
        }
        if (-not $anchors.ContainsKey($fragment)) { Fail "$description broken anchor: $link" }
    }    return $full
}
function IsUuid($v) { return $v -is [string] -and $v -cmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-8][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$' }
function IsUtc($v) { return $v -is [string] -and $v -cmatch '^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?Z$' -and [datetimeoffset]::TryParse($v,[ref]([datetimeoffset]::MinValue)) }
function ValidateProjection($v) {
    ExactKeys $v @('version','generation','entries','messages','actions') 'projection'
    Need ($v.version -is [int] -and $v.version -eq 1) 'projection version must be integer 1'
    Need ($v.generation -is [long] -or $v.generation -is [int]) 'generation must be integer'
    Need ($v.generation -ge 0) 'generation must be nonnegative'
    foreach ($group in @('entries','messages','actions')) { Need ($v.$group -is [array]) "$group must be an array" }
    foreach ($e in @($v.entries)) {
        ExactKeys $e @('id','holds','seenAt') 'Entry'
        Need (IsUuid $e.id) 'Entry.id must be UUID'
        Need (IsUtc $e.seenAt) 'Entry.seenAt must be UTC RFC3339'
        Need ($null -ne $e.holds -and $e.holds -is [pscustomobject]) 'Entry.holds must be object'
        foreach ($h in $e.holds.PSObject.Properties) {
            Need ($h.Name -cmatch '^(file|id):.+$') "invalid Resource $($h.Name)"
            Need ($h.Value -cin @('read','write')) "invalid hold $($h.Value)"
        }
    }
    foreach ($m in @($v.messages)) {
        $keys=@('id','from','to','text','createdAt'); if ((Properties $m) -ccontains 'replyTo') { $keys+= 'replyTo' }
        ExactKeys $m $keys 'Message'
        Need (IsUuid $m.id) 'Message.id must be UUID'
        foreach ($field in @('from','to')) { Need (ValidAddress $m.$field) "Message.$field invalid address" }
        Need ($m.text -is [string]) 'Message.text must be string'
        Need (IsUtc $m.createdAt) 'Message.createdAt must be UTC RFC3339'
        if ((Properties $m) -ccontains 'replyTo') { Need (IsUuid $m.replyTo) 'Message.replyTo must be UUID' }
    }
    foreach ($a in @($v.actions)) {
        $keys=@('id','text','state','createdAt'); if ((Properties $a) -ccontains 'owner') { $keys+= 'owner' }
        ExactKeys $a $keys 'Action'
        Need (IsUuid $a.id) 'Action.id must be UUID'
        if ((Properties $a) -ccontains 'owner') { Need (ValidAddress $a.owner) 'Action.owner invalid address' }
        Need ($a.text -is [string]) 'Action.text must be string'
        Need ($a.state -cin @('open','closed')) 'Action.state invalid literal'
        Need (IsUtc $a.createdAt) 'Action.createdAt must be UTC RFC3339'
    }
}
function ValidAddress($v) {
    return $v -is [string] -and ($v -cin @('user','system','agents','*') -or (IsUuid $v) -or $v -cmatch '^group:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-8][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$')
}
function Validate {
    $script:failures.Clear()
    $rootFull=[IO.Path]::GetFullPath($Root)
    $catalogueFile=Join-Path $rootFull 'docs/kernel-acceptance.json'
    $planFile=Join-Path $rootFull 'docs/coordination-kernel.md'
    $referenceFile=Join-Path $rootFull 'docs/reference-coverage.md'
    $cliFile=Join-Path $rootFull 'src/AgentOS.Cli/Program.cs'
    foreach ($f in @($catalogueFile,$planFile,$referenceFile,$cliFile,(Join-Path $rootFull 'knowledge/coordination-kernel.md'))) {
        if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { Fail "required file missing: $f" }
    }
    if ($script:failures.Count) { return }
    try { $catalogue=Get-Content -LiteralPath $catalogueFile -Raw | ConvertFrom-Json }
    catch { Fail "catalogue malformed JSON: $_"; return }
    ExactKeys $catalogue @('version','source','historicalCatalogue','requirements') 'catalogue'
    Need ($catalogue.version -is [int] -and $catalogue.version -eq 1) 'catalogue version must be 1'
    Need ($catalogue.requirements -is [array]) 'requirements must be array'
    $plan=Get-Content -LiteralPath $planFile -Raw
    $ref=Get-Content -LiteralPath $referenceFile -Raw
    $cli=Get-Content -LiteralPath $cliFile -Raw
    Need ($catalogue.source -ceq 'docs/coordination-kernel.md') 'catalogue source path changed'
    Need ($catalogue.historicalCatalogue -ceq 'docs/reference-coverage.md') 'catalogue historical source path changed'
    $planCases=@{}
    foreach ($m in [regex]::Matches($plan,'(?m)^\| ((?:OV|DP|AW|ST|AK|PR|LF|DL|HS)-\d+) \| ([^|]+) \|')) {
        $id=$m.Groups[1].Value
        Need (-not $planCases.ContainsKey($id)) "plan duplicate ID $id"
        $planCases[$id]=$m.Groups[2].Value.Trim()
    }
    Need ($planCases.Count -eq 85) 'plan mapping must contain 85 unique legacy IDs'
    $legacy=@{}
    foreach ($m in [regex]::Matches($ref,'(?m)^\| \d+ \| ((?:OV|DP|AW|ST|AK|PR|LF|DL|HS)-\d+) \| ([^|]+) \|')) {
        $legacy[$m.Groups[1].Value]=$m.Groups[2].Value.Trim()
    }
    $expected=@{}
    foreach ($range in @(@('OV',11),@('DP',9),@('AW',9),@('ST',7),@('AK',8),@('PR',14),@('LF',8),@('DL',13),@('HS',6))) {
        for ($n=1;$n -le $range[1];$n++) { $expected["$($range[0])-$n"]=$true }
    }
    Need ($expected.Count -eq 85) 'internal legacy manifest error'
    Need ($legacy.Count -eq 85) 'historical reference must contain 85 unique scenarios'
    foreach ($id in $expected.Keys) { Need ($planCases.ContainsKey($id)) "plan missing ID $id"; if ($planCases.ContainsKey($id) -and $legacy.ContainsKey($id)) { Need ($planCases[$id] -ceq $legacy[$id]) "plan scenario changed for $id" } }
    $baselineFields=@(
        'ProjectState schema 3','machine journal schema 2',
        'WorkUnit Id/Task/Title/Status/Detail',
        'WorkUnit Workspace/BaseCommit/CandidateCommit/IntegratedCommit/PendingCommit/ChangedPaths/Diff',
        'WorkUnit ThreadId/HostVersion/HostModel/ExecutionProfile/CreatedAt/UpdatedAt',
        'WorkUnit Evidence/ContextRefs/ModulePins/PublishedPathObjects',
        'TaskMap edges/citations/selected/pending',
        'InteractiveWork messages/waits/obligations',
        'ConflictNotice/HumanDecision',
        'MachineCoordinator claims/process owner',
        'PrivateGit/validation/release',
        'AppContainer/Hyper-V/external effects',
        'Capture/context drafting',
        'Desktop compact UI/transcript/diff/maps/citations/inbox',
        'Notices/tray/close guard/update'
    )
    foreach ($field in $baselineFields) { Need ($plan.Contains($field)) "baseline field mapping missing: $field" }
    $semantic=@{}
    foreach ($r in @(@('ARCH',6),@('SCHEMA',10),@('PROJ',6),@('TX',7),@('API',9),@('SCHED',7),@('LIFE',2),@('POLICY',4),@('EVID',3),@('RET',2),@('BACK',1),@('MIG',3),@('VERIFY',7),@('WIN',4))) {
        for ($n=1;$n -le $r[1];$n++) { $semantic[('{0}-{1:D2}' -f $r[0],$n)]=$true }
    }
    $seen=@{};$counts=@{planned=0;implemented=0;verified=0;excluded=0}
    foreach ($q in $catalogue.requirements) {
        ExactKeys $q @('id','scenario','owner','operation','status','platform','rationale','implementation','evidence') 'requirement'
        if ($q.id -isnot [string] -or !$q.id) { Fail 'requirement missing id'; continue }
        if ($seen.ContainsKey($q.id)) { Fail "duplicate ID $($q.id)" };$seen[$q.id]=$true
        Need ($expected.ContainsKey($q.id) -or $semantic.ContainsKey($q.id)) "unexpected ID $($q.id)"
        foreach ($field in @('scenario','owner','operation','platform','rationale')) { Need ($q.$field -is [string] -and $q.$field.Trim()) "$($q.id) missing $field" }
        Need ($q.status -cin @('planned','implemented','verified','excluded')) "$($q.id) invalid status"
        if ($counts.ContainsKey($q.status)) { $counts[$q.status]++ }
        if ($expected.ContainsKey($q.id)) {
            Need ($legacy.ContainsKey($q.id)) "$($q.id) absent in historical reference"
            if ($legacy.ContainsKey($q.id)) { Need ($q.scenario -ceq $legacy[$q.id]) "$($q.id) scenario meaning changed" }
        }
        if ($q.status -eq 'excluded') {
            Need ($q.id -match '^(DL-(4|9|10|11|12|13)|HS-(1|5))$') "$($q.id) exclusion outside allowed platforms"
            Need ($q.platform -match 'Mac|phone|host') "$($q.id) exclusion missing platform"
            Need ($q.rationale.Length -ge 20) "$($q.id) exclusion missing rationale"
        } else {
            Need ($q.platform -ceq 'Windows/Codex') "$($q.id) must map Windows/Codex"
        }
        if ($q.status -in @('implemented','verified')) { Need ($q.implementation -is [string] -and $q.implementation) "$($q.id) implemented anchor required" }
        if ($q.implementation -and $q.status -in @('implemented','verified')) { [void](ResolveRepoPath $rootFull $q.implementation "$($q.id) implementation") }
        if ($q.status -eq 'verified') { Need ($null -ne $q.evidence) "$($q.id) verified evidence required" }
        if ($null -ne $q.evidence) {
            ExactKeys $q.evidence @('command','sourceRevision','result','artifact','sha256') "$($q.id) evidence"
            Need ($q.status -eq 'verified') "$($q.id) executed evidence requires verified status"
            Need ($q.evidence.command -is [string] -and $q.evidence.command.Trim()) "$($q.id) evidence command required"
            Need ($q.evidence.sourceRevision -cmatch '^[0-9a-f]{7,40}$') "$($q.id) source revision invalid"
            Need ($q.evidence.result -cin @('passed','failed','unknown')) "$($q.id) evidence result invalid"
            Need ($q.evidence.result -ceq 'passed') "$($q.id) verified status requires passed result"
            Need ($q.evidence.sha256 -cmatch '^[0-9a-fA-F]{64}$') "$($q.id) artifact digest invalid"
            if ($q.evidence.artifact) {
                $artifact=ResolveRepoPath $rootFull $q.evidence.artifact "$($q.id) artifact"
                if ($artifact) { Need ((Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash -ieq $q.evidence.sha256) "$($q.id) artifact digest mismatch" }
            } else { Fail "$($q.id) artifact receipt required" }
        }
    }
    foreach ($id in $expected.Keys) { Need ($seen.ContainsKey($id)) "missing ID $id" }
    foreach ($id in $semantic.Keys) { Need ($seen.ContainsKey($id)) "missing semantic ID $id" }
    Need ($counts.planned+$counts.implemented+$counts.verified+$counts.excluded -eq $expected.Count+$semantic.Count) 'requirement count differs from fixed manifest'
    $script:counts=$counts
    $types=@(
        ('type Resource = ' + [char]96 + 'file:' + [char]36 + '{string}' + [char]96 + ' | ' + [char]96 + 'id:' + [char]36 + '{string}' + [char]96 + ';'),
        'type Address = string;',
        'type Registry = {version:1;generation:number;entries:Entry[];messages:Message[];actions:Action[]};',
        "type Entry = {id:string;holds:Record<Resource,'read'|'write'>;seenAt:string};",
        'type Message = {id:string;from:Address;to:Address;text:string;replyTo?:string;createdAt:string};',
        "type Action = {id:string;owner?:Address;text:string;state:'open'|'closed';createdAt:string};"
    )
    $tick3=([string][char]96)*3
    $block=[regex]::Match($plan,'(?s)'+[regex]::Escape($tick3)+'typescript\s*\r?\n(.*?)\r?\n'+[regex]::Escape($tick3))
    Need $block.Success 'exact public TypeScript block missing'
    if ($block.Success) {
        $actual=@($block.Groups[1].Value -split '\r?\n' | Where-Object { $_.Trim() })
        Need ($actual.Count -eq 6) 'public TypeScript declaration count must be six'
        for ($i=0;$i -lt [Math]::Min($actual.Count,6);$i++) { Need ($actual[$i] -ceq $types[$i]) "public TypeScript declaration $($i+1) differs" }
    }
    Need ($plan -match 'bare UUID participant') 'bare UUID participant address missing'
    Need ($plan -match 'group:<id>') 'group address grammar missing'
    Need ($plan -match 'agents and \*') 'agents and broadcast address grammar missing'
    $example=[regex]::Match($plan,'(?s)'+[regex]::Escape($tick3)+'kernel-projection-example\s*\r?\n(.*?)\r?\n'+[regex]::Escape($tick3))
    Need $example.Success 'kernel-projection-example block missing'
    if ($example.Success) {
        try { $value=$example.Groups[1].Value | ConvertFrom-Json; ValidateProjection $value }
        catch { Fail "projection example malformed JSON: $_" }
    }
    foreach ($document in @('docs/coordination-kernel.md','knowledge/coordination-kernel.md')) {
        $path=Join-Path $rootFull $document
        $body=Get-Content -LiteralPath $path -Raw
        foreach ($link in [regex]::Matches($body,'(?<!!)\[[^\]]+\]\(([^)]+)\)')) {
            $target=$link.Groups[1].Value.Trim()
            if ($target -match '^(https?:|mailto:|#)') { continue }
            [void](ResolveRepoPath (Split-Path -Parent $path) $target "$document link")
        }
    }
    $verbs=@{}
    foreach ($m in [regex]::Matches($cli,'(?m)^\s*case "([a-z][a-z0-9-]*)":')) { $verbs[$m.Groups[1].Value]=$true }
    foreach ($m in [regex]::Matches($plan,'(?m)^\s*(?:>\s*)?(?:'+[regex]::Escape([string][char]96)+')?agent-os\s+([a-z][a-z0-9-]*)\b')) {
        Need ($verbs.ContainsKey($m.Groups[1].Value)) "public command example uses unimplemented CLI verb $($m.Groups[1].Value)"
    }
    if ($RequireComplete) {
        foreach ($q in $catalogue.requirements) {
            if ($q.status -notin @('verified','excluded')) { Fail "$($q.id) remains $($q.status)" }
            if ($q.status -eq 'verified' -and $q.evidence.result -ne 'passed') { Fail "$($q.id) has no passing receipt" }
        }
    }
    Write-Output ("Kernel docs: planned={0} implemented={1} verified={2} excluded={3}; checks: manifest, historical meanings, schema example, TypeScript, links, CLI, evidence, strict={4}" -f $counts.planned,$counts.implemented,$counts.verified,$counts.excluded,[bool]$RequireComplete)
}
if ($SelfCheck) {
    $token=[guid]::NewGuid().ToString('N')
    $selfCheckName='.kernel-doc-selfcheck-'+$token
    $tmp=Join-Path ([IO.Path]::GetFullPath($Root)) $selfCheckName
    $names=@('missing ID','duplicate ID','malformed schema example','broken link','broken anchor','unimplemented command','evidence without receipt')
    try {
        foreach ($name in $names) {
            $f=Join-Path $tmp ($name -replace '[^a-zA-Z0-9]','-')
            New-Item -ItemType Directory -Path $f -Force | Out-Null
            Copy-Item (Join-Path $Root 'docs') (Join-Path $f 'docs') -Recurse
            Copy-Item (Join-Path $Root 'knowledge') (Join-Path $f 'knowledge') -Recurse
            New-Item -ItemType Directory -Path (Join-Path $f 'src/AgentOS.Cli') -Force | Out-Null
            Copy-Item (Join-Path $Root 'src/AgentOS.Cli/Program.cs') (Join-Path $f 'src/AgentOS.Cli/Program.cs')
            New-Item -ItemType Directory -Path (Join-Path $f 'scripts') -Force | Out-Null
            Copy-Item (Join-Path $Root 'scripts/ValidateKernelDocs.ps1') (Join-Path $f 'scripts/ValidateKernelDocs.ps1')
            $sourceCatalogue=Get-Content (Join-Path $Root 'docs/kernel-acceptance.json') -Raw|ConvertFrom-Json
            foreach ($q in $sourceCatalogue.requirements) {
                foreach ($rel in @($q.implementation,$q.evidence.artifact)) {
                    if ([string]::IsNullOrWhiteSpace($rel)) { continue }
                    $src=ResolveRepoPath ([IO.Path]::GetFullPath($Root)) $rel 'fixture source'
                    if (!$src) { throw "missing fixture source $rel" }
                    $dst=Join-Path $f $rel
                    New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
                    Copy-Item -LiteralPath $src -Destination $dst
                }
            }
            $control=(& $PSCommandPath -Root $f 2>&1 | Out-String)
            if ($LASTEXITCODE -ne 0) { throw "unchanged positive control failed: $control" }            $cf=Join-Path $f 'docs/kernel-acceptance.json'
            $pf=Join-Path $f 'docs/coordination-kernel.md'
            if ($name -in @('missing ID','duplicate ID','evidence without receipt')) {
                $c=Get-Content $cf -Raw|ConvertFrom-Json
                if ($name -eq 'missing ID') { $c.requirements=@($c.requirements|Where-Object {$_.id -ne 'OV-1'}) }
                if ($name -eq 'duplicate ID') { $c.requirements+= $c.requirements[0] }
                if ($name -eq 'evidence without receipt') { $c.requirements[0].status='verified';$c.requirements[0].implementation='src/AgentOS.Cli/Program.cs' }
                $c|ConvertTo-Json -Depth 10|Set-Content $cf -Encoding UTF8
            } else {
                $d=Get-Content $pf -Raw
                if ($name -eq 'malformed schema example') { $d=$d.Replace('"version": 1','"version": "invalid"') }
                if ($name -eq 'broken link') { $d+=[Environment]::NewLine+'[broken](missing-kernel-target.md)' }
                if ($name -eq 'broken anchor') { $d+=[Environment]::NewLine+'[broken](guarantees.md#no-such-heading)' }
                if ($name -eq 'unimplemented command') { $d+=[Environment]::NewLine+'agent-os invented-kernel-verb' }
                Set-Content $pf $d -Encoding UTF8
            }
            $output=(& $PSCommandPath -Root $f 2>&1 | Out-String)
            if ($LASTEXITCODE -eq 0) { throw "negative fixture unexpectedly passed: $name" }
            $needles=@{'missing ID'='missing ID OV-1';'duplicate ID'='duplicate ID OV-1';'malformed schema example'='projection version must be integer 1';'broken link'='broken: missing-kernel-target.md';'broken anchor'='broken anchor: guarantees.md#no-such-heading';'unimplemented command'='unimplemented CLI verb invented-kernel-verb';'evidence without receipt'='verified evidence required'}
            if ($output -notmatch [regex]::Escape($needles[$name])) { throw "negative fixture failed for wrong reason: $name; $output" }
            Write-Output "negative fixture rejected: $name"
        }
    } finally {
        if (Test-Path -LiteralPath $tmp) {
            $rootPath=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')
            $targetPath=[IO.Path]::GetFullPath($tmp)
            $expectedPath=Join-Path $rootPath $selfCheckName
            if ($selfCheckName -cnotmatch '^\.kernel-doc-selfcheck-[0-9a-f]{32}$' -or
                [IO.Path]::GetFileName($targetPath) -cne $selfCheckName -or
                -not $targetPath.Equals($expectedPath,[StringComparison]::OrdinalIgnoreCase) -or
                -not $targetPath.StartsWith($rootPath+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
                throw "self-check cleanup target failed root and GUID containment check root=$rootPath target=$targetPath expected=$expectedPath name=$selfCheckName"
            }
            $stack=New-Object System.Collections.Stack
            $stack.Push($targetPath)
            while ($stack.Count) {
                $dir=[string]$stack.Pop()
                $item=Get-Item -LiteralPath $dir -Force
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "self-check cleanup refuses reparse point: $dir" }
                foreach ($child in (Get-ChildItem -LiteralPath $dir -Force)) {
                    if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "self-check cleanup refuses reparse point: $($child.FullName)" }
                    if ($child.PSIsContainer) { $stack.Push($child.FullName) }
                }
            }
            Remove-Item -LiteralPath $targetPath -Recurse -Force
        }
    }
    exit 0
}
Validate
if ($script:failures.Count) {
    foreach ($failure in $script:failures) { Write-Error $failure -ErrorAction Continue }
    exit 1
}
exit 0

















