param([Parameter(Mandatory)][string]$Package,[Parameter(Mandatory)][string]$Manifest,[Parameter(Mandatory)][string]$InstallRoot,[Parameter(Mandatory)][string]$Project,[Parameter(Mandatory)][string]$CliPath,[Parameter(Mandatory)][string]$TrustedSignerThumbprint,[switch]$VerifyOnly)
$ErrorActionPreference='Stop'
function Reject($m){throw "Update rejected: $m"}
function Unavailable($m){throw "Update unavailable: $m"}
function PlainFile($path){if(Test-Path -LiteralPath $path){$item=Get-Item -LiteralPath $path -Force;if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){Reject 'linked or nonfile update artifact'}}}
function PlainPath($path){for($cursor=[IO.DirectoryInfo]::new($path);$cursor;$cursor=$cursor.Parent){if($cursor.Exists -and ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint)){Reject 'update path traverses a reparse point'}}}
function Overlap($a,$b){$a=[IO.Path]::GetFullPath($a).TrimEnd('\');$b=[IO.Path]::GetFullPath($b).TrimEnd('\');return $a -eq $b -or $a.StartsWith($b+'\',[StringComparison]::OrdinalIgnoreCase) -or $b.StartsWith($a+'\',[StringComparison]::OrdinalIgnoreCase)}
function SaveJson($path,$value){$next=$path+'.next';PlainFile $path;PlainFile $next;$bytes=[Text.UTF8Encoding]::new($false).GetBytes(($value|ConvertTo-Json -Depth 8));$stream=[IO.File]::Open($next,'Create','Write','None');try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()};Move-Item -LiteralPath $next -Destination $path -Force}
function Signed($path,$thumb){PlainFile $path;if(!(Test-Path -LiteralPath $path -PathType Leaf)){Reject "signed package missing: $path"};$s=Get-AuthenticodeSignature -LiteralPath $path;if($s.Status -ne 'Valid' -or !$s.SignerCertificate -or $s.SignerCertificate.Thumbprint -ne $thumb){Reject 'invalid package signature or signer'}}
function Certificate($path,$publisher,$thumb){$c=(Get-AuthenticodeSignature -LiteralPath $path).SignerCertificate;if(!$c -or $c.Thumbprint -ne $thumb -or $c.Subject -cne $publisher){Reject 'signed publisher or signer differs from MSIX identity'};if($c.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow -or $c.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow){Reject 'signer certificate is outside validity period'};$eku=$c.Extensions|Where-Object {$_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]}|Select-Object -First 1;if(!$eku -or !($eku.EnhancedKeyUsages|Where-Object Value -eq '1.3.6.1.5.5.7.3.3')){Reject 'signer lacks Code Signing EKU'};$chain=[Security.Cryptography.X509Certificates.X509Chain]::new();try{if(!$chain.Build($c)){Reject 'signer chain is not trusted'}}finally{$chain.Dispose()}}
function Identity($path){Add-Type -AssemblyName System.IO.Compression.FileSystem;$z=[IO.Compression.ZipFile]::OpenRead($path);try{$e=$z.GetEntry('AppxManifest.xml');if(!$e -or $e.Length -gt 1048576){Reject 'MSIX identity missing or too large'};$r=[IO.StreamReader]::new($e.Open());try{[xml]$x=$r.ReadToEnd()}finally{$r.Dispose()};$i=$x.SelectSingleNode('/*[local-name()="Package"]/*[local-name()="Identity"]');$d=$x.SelectSingleNode('/*[local-name()="Package"]/*[local-name()="Dependencies"]/*[local-name()="TargetDeviceFamily"]');if(!$i -or !$d){Reject 'MSIX identity or minimum OS missing'};return @{Name=$i.GetAttribute('Name');Publisher=$i.GetAttribute('Publisher');Version=$i.GetAttribute('Version');Arch=$i.GetAttribute('ProcessorArchitecture');MinOS=$d.GetAttribute('MinVersion')}}finally{$z.Dispose()}}
function StateRefs($path){@(Get-ChildItem -LiteralPath $path -Recurse -Force|Sort-Object FullName|ForEach-Object {if($_.Attributes -band [IO.FileAttributes]::ReparsePoint){Reject 'project state contains a linked reference'};$relative=$_.FullName.Substring($path.Length);if($_.PSIsContainer){'D|'+$relative}else{'F|'+$relative+'|'+(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})}
$pkg=[IO.Path]::GetFullPath($Package);$manifest=[IO.Path]::GetFullPath($Manifest);$root=[IO.Path]::GetFullPath($InstallRoot);$projectPath=[IO.Path]::GetFullPath($Project);$cli=[IO.Path]::GetFullPath($CliPath);$thumb=($TrustedSignerThumbprint -replace '\s','').ToUpperInvariant()
if($thumb -notmatch '^[A-F0-9]{40}$'){Reject 'trusted signer thumbprint is invalid'}
if(!(Get-Command Get-AuthenticodeSignature -ErrorAction SilentlyContinue)){Unavailable 'Authenticode tool missing'}
if(!(Get-Command Add-AppxPackage -ErrorAction SilentlyContinue) -and !$VerifyOnly){Unavailable 'MSIX installer missing'}
$trusted=Get-ChildItem Cert:\LocalMachine\TrustedPeople,Cert:\CurrentUser\TrustedPeople,Cert:\LocalMachine\TrustedPublisher,Cert:\CurrentUser\TrustedPublisher -ErrorAction SilentlyContinue|Where-Object Thumbprint -eq $thumb|Select-Object -First 1
if(!$trusted){Unavailable 'trusted signer certificate missing'}
if(!(Test-Path $manifest -PathType Leaf)){Reject 'package manifest missing'}
if(!(Test-Path $root -PathType Container)){Unavailable 'installation root missing'};PlainPath $root
if(!(Test-Path $projectPath -PathType Container)){Unavailable 'project missing'};if(Overlap $root $projectPath){Reject 'update state overlaps project checkout'}
if(!(Test-Path $cli -PathType Leaf)){Unavailable 'packaged CLI missing'}
if(!(Test-Path $pkg -PathType Leaf)){Reject 'signed package missing'}
$spec=Get-Content $manifest -Raw|ConvertFrom-Json;$sha=(Get-FileHash $pkg -Algorithm SHA256).Hash
if($spec.Schema -ne 1 -or $spec.Protocol -ne 1 -or $spec.StateSchema -ne 3 -or $spec.Diagnostic -or $spec.Package -ne [IO.Path]::GetFileName($pkg) -or $sha -ne $spec.Sha256 -or $spec.SignerThumbprint -ne $thumb -or $spec.SourceSha256 -notmatch '^[A-F0-9]{64}$' -or $spec.BinarySha256 -notmatch '^[A-F0-9]{64}$' -or $spec.AppBinarySha256 -notmatch '^[A-F0-9]{64}$'){Reject 'manifest, hash, or signer mismatch'}
Signed $pkg $thumb;$id=Identity $pkg;Certificate $pkg $id.Publisher $thumb
$archive=[IO.Compression.ZipFile]::OpenRead($pkg);try{
 $entry=$archive.GetEntry('update-protocol.json');if(!$entry -or $entry.Length -gt 65536){Reject 'signed protocol metadata missing or too large'};$reader=[IO.StreamReader]::new($entry.Open());try{$embedded=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
 foreach($part in @(@('AgentOS.Core.dll',$spec.AppBinarySha256),@('cli/AgentOS.Core.dll',$spec.BinarySha256))){$e=$archive.GetEntry($part[0]);if(!$e){Reject "signed runtime assembly missing: $($part[0])"};$stream=$e.Open();$hasher=[Security.Cryptography.SHA256]::Create();try{$actual=([BitConverter]::ToString($hasher.ComputeHash($stream)) -replace '-','');if($actual -ne $part[1]){Reject 'signed runtime binary metadata mismatch'}}finally{$stream.Dispose();$hasher.Dispose()}}
}finally{$archive.Dispose()}
if($embedded.Protocol -ne $spec.Protocol -or $embedded.StateSchema -ne $spec.StateSchema -or $embedded.SourceSha256 -ne $spec.SourceSha256 -or $embedded.BinarySha256 -ne $spec.BinarySha256 -or $embedded.AppBinarySha256 -ne $spec.AppBinarySha256){Reject 'external protocol metadata differs from signed package'}
if($id.Name -ne 'AgentOS.Desktop' -or $id.Name -ne $spec.IdentityName -or $id.Publisher -cne $spec.Publisher -or $id.Version -ne $spec.Version -or $id.Arch -ne 'x64' -or ![Environment]::Is64BitOperatingSystem){Reject 'MSIX version or architecture mismatch'}
if([version]$id.MinOS -gt [Environment]::OSVersion.Version){Unavailable 'host OS below MSIX minimum'}
$current=Join-Path $root 'package-manifest.json';PlainFile $current;if(!(Test-Path $current -PathType Leaf)){Unavailable 'installed package metadata missing'}
$old=Get-Content $current -Raw|ConvertFrom-Json
if($old.Schema -ne 1 -or $old.SignerThumbprint -ne $thumb){Reject 'installed package metadata schema or signer mismatch'}
if($id.Name -ne $old.IdentityName -or $id.Publisher -cne $old.Publisher -or [version]$id.Version -le [version]$old.Version){Reject 'MSIX identity or newer version mismatch'}
if(!$old.PackageFile){Unavailable 'prior signed package reference missing'}
$prior=[IO.Path]::GetFullPath($old.PackageFile);Signed $prior $thumb
if((Get-FileHash $prior -Algorithm SHA256).Hash -ne $old.Sha256){Reject 'prior package hash mismatch'}
$oldId=Identity $prior;Certificate $prior $oldId.Publisher $thumb;if($oldId.Name -ne $id.Name -or $oldId.Publisher -cne $id.Publisher -or $oldId.Version -ne $old.Version -or $oldId.Arch -ne 'x64' -or [version]$oldId.MinOS -gt [Environment]::OSVersion.Version){Reject 'prior package identity or host compatibility mismatch'}
if($VerifyOnly){'Signed package and reversible prior package verified.';return}
if(!(Get-Command Get-AppxPackage -ErrorAction SilentlyContinue)){Unavailable 'installed MSIX inspection tool missing'}
$priorApp=Get-AppxPackage -Name $id.Name|Where-Object {$_.Version.ToString() -eq $old.Version -and $_.Publisher -eq $id.Publisher}|Select-Object -First 1
if(!$priorApp){Unavailable 'installed MSIX differs from prior signed package metadata'};if(Overlap $root $priorApp.InstallLocation){Reject 'update state overlaps installed runtime'}
if(!$old.InstallLocation -or !$old.PackageFamilyName -or [IO.Path]::GetFullPath($old.InstallLocation) -ne [IO.Path]::GetFullPath($priorApp.InstallLocation) -or $old.PackageFamilyName -ne $priorApp.PackageFamilyName){Reject 'target installation differs from installed metadata'}
$expectedCli=Join-Path $priorApp.InstallLocation 'cli/AgentOS.Cli.exe'
if([IO.Path]::GetFullPath($cli) -ne [IO.Path]::GetFullPath($expectedCli)){Reject 'CLI outside target installed package'}
$oldCore=Join-Path $priorApp.InstallLocation 'AgentOS.Core.dll';if(!(Test-Path $oldCore -PathType Leaf)){Unavailable 'installed owning runtime assembly missing'}
$health=& $cli update-health $projectPath|ConvertFrom-Json
if($LASTEXITCODE -ne 0 -or $health.AssemblySha256 -ne (Get-FileHash $oldCore -Algorithm SHA256).Hash -or $health.Version -ne 1 -or $health.StateSchema -ne 3 -or (@('scoped-drain','cooperative-exit','schema3','signed-msix-handshake')|Where-Object {$_ -notin $health.Capabilities}).Count -ne 0){Unavailable 'owning runtime protocol unavailable or incompatible'}
$dir=Join-Path $root 'update-state';PlainPath $dir;New-Item -ItemType Directory -Path $dir -Force|Out-Null
$pendingPath=Join-Path $dir 'pending.json';PlainFile $pendingPath;$previous=$null
if(Test-Path $pendingPath -PathType Leaf){$previous=Get-Content $pendingPath -Raw|ConvertFrom-Json;if($previous.Status -in @('Installing','RecoveryRequired')){Unavailable 'prior update needs explicit recovery'}}
if($previous -and $previous.Status -in @('WaitingForDrain','Aborted') -and ($previous.PackageSha256 -ne $sha -or $previous.Version -ne $id.Version -or $previous.Project -ne $projectPath)){Unavailable 'another signed package owns the pending drain'}
$reuse=$previous -and $previous.Status -in @('WaitingForDrain','Aborted') -and $previous.PackageSha256 -eq $sha -and $previous.Version -eq $id.Version -and $previous.Project -eq $projectPath -and (Test-Path $previous.Backup -PathType Container)
if($reuse){$backup=[IO.Path]::GetFullPath($previous.Backup);if(!$backup.StartsWith($dir+'\',[StringComparison]::OrdinalIgnoreCase)){Reject 'saved drain backup outside update state'};Signed (Join-Path $backup 'prior.msix') $thumb;if((Get-FileHash (Join-Path $backup 'prior.msix') -Algorithm SHA256).Hash -ne $old.Sha256){Reject 'saved rollback package changed'}}
else{$backup=Join-Path $dir ([guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $backup|Out-Null;if(Test-Path $pendingPath -PathType Leaf){Copy-Item $pendingPath (Join-Path $backup 'previous-pending.json')};Copy-Item $prior (Join-Path $backup 'prior.msix');Copy-Item $current (Join-Path $backup 'prior.json')}
PlainPath $backup;PlainFile (Join-Path $backup 'prior.msix');PlainFile (Join-Path $backup 'prior.json');$candidate=Join-Path $backup 'candidate.msix';PlainFile $candidate;if(!$reuse -or $previous.Status -eq 'Aborted'){Copy-Item $pkg $candidate -Force};Signed $candidate $thumb
if((Get-FileHash $candidate -Algorithm SHA256).Hash -ne $sha -or (Get-FileHash (Join-Path $backup 'prior.msix') -Algorithm SHA256).Hash -ne $old.Sha256){Reject 'staged package hash mismatch'}
$drain=& $cli update-drain $projectPath $sha $id.Version $id.Arch $root $candidate $thumb|ConvertFrom-Json
if($LASTEXITCODE -ne 0 -or !$drain.Token -or $drain.Scope.PackageSha256 -ne $sha){Unavailable 'owning runtime refused exact package drain'}
SaveJson $pendingPath @{Status='WaitingForDrain';PackageSha256=$sha;Version=$id.Version;Project=$projectPath;Backup=$backup}
$ready=& $cli update-ready $projectPath $drain.Token|ConvertFrom-Json
if($LASTEXITCODE -ne 0 -or !$ready.Ready){SaveJson $pendingPath @{Status='WaitingForDrain';PackageSha256=$sha;Version=$id.Version;Project=$projectPath;Blockers=$ready.Blockers;Backup=$backup};Unavailable ('runtime not ready: '+($ready.Blockers -join ', '))}
$generation=$ready.StateGeneration;$schema=$ready.Protocol.StateSchema;$statePath=$ready.StatePath
if(!$statePath -or !(Test-Path $statePath -PathType Leaf)){Unavailable 'project state reference missing'}
$stateRoot=Split-Path $statePath -Parent;PlainPath $stateRoot;$refs=StateRefs $stateRoot
SaveJson (Join-Path $backup 'state-refs.json') @{StatePath=$statePath;Entries=$refs}
& $cli update-exit $projectPath $drain.Token|Out-Null;if($LASTEXITCODE -ne 0){Unavailable 'owning runtime refused cooperative exit'}
$lock=Join-Path $projectPath '.git\agent-os.runtime.lock';$lockLease=$null
for($n=0;$n -lt 60;$n++){try{$lockLease=[IO.File]::Open($lock,'Open','ReadWrite','None');break}catch{Start-Sleep -Milliseconds 500}}
if(!$lockLease){Unavailable 'owning runtime lock still held; install not attempted'}
if(Get-Process AgentOS,AgentOS.Cli -ErrorAction SilentlyContinue){$lockLease.Dispose();Unavailable 'another AgentOS window remains open; it was not closed'}
if(((StateRefs $stateRoot) -join "`n") -ne ($refs -join "`n")){$lockLease.Dispose();Unavailable 'project state changed after readiness; install not attempted'}
$record=@{Status='Installing';PackageSha256=$sha;Version=$id.Version;Project=$projectPath;Backup=$backup;BeforeGeneration=$generation;BeforeSchema=$schema;StatePath=$statePath;CreatedUtc=[DateTime]::UtcNow.ToString('o')}
$installAttempted=$false
try{
 SaveJson $pendingPath $record
 Signed $candidate $thumb
 if((Get-FileHash $candidate -Algorithm SHA256).Hash -ne $sha){throw 'package hash changed after drain'}
 $packageLease=[IO.File]::Open($candidate,'Open','Read','Read');try{if((Get-FileHash $candidate -Algorithm SHA256).Hash -ne $sha){throw 'package hash changed under lease'};$installAttempted=$true;Add-AppxPackage -Path $candidate -ErrorAction Stop}finally{$packageLease.Dispose()}
 $app=Get-AppxPackage -Name $id.Name|Where-Object {$_.Version.ToString() -eq $id.Version -and $_.Publisher -eq $id.Publisher}|Select-Object -First 1
 if(!$app -or $app.PackageFamilyName -ne $old.PackageFamilyName){throw 'installed MSIX identity or family not found'}
 $nextCli=Join-Path $app.InstallLocation 'cli/AgentOS.Cli.exe';$core=Join-Path $app.InstallLocation 'cli/AgentOS.Core.dll';$appCore=Join-Path $app.InstallLocation 'AgentOS.Core.dll'
 if(!(Test-Path $nextCli -PathType Leaf) -or !(Test-Path $core -PathType Leaf) -or !(Test-Path $appCore -PathType Leaf)){throw 'new packaged runtime assembly or CLI missing'}
 $hash=(Get-FileHash $core -Algorithm SHA256).Hash
 if($hash -ne $spec.BinarySha256 -or (Get-FileHash $appCore -Algorithm SHA256).Hash -ne $spec.AppBinarySha256){throw 'installed runtime binary differs from signed package manifest'}
 $handshake=& $nextCli update-handshake 1 3 $hash|ConvertFrom-Json
 if($LASTEXITCODE -ne 0 -or $handshake.Version -ne 1 -or $handshake.StateSchema -gt 3 -or $handshake.AssemblySha256 -ne $hash -or (@('scoped-drain','cooperative-exit','schema3','signed-msix-handshake')|Where-Object {$_ -notin $handshake.Capabilities}).Count -ne 0){throw 'new CLI protocol, schema, assembly hash or capability handshake failed'}
 if(((StateRefs $stateRoot) -join "`n") -ne ($refs -join "`n")){throw 'project state changed during installation'}
 SaveJson $current @{Schema=1;PackageFile=$candidate;Sha256=$sha;Version=$id.Version;IdentityName=$id.Name;Publisher=$id.Publisher;SignerThumbprint=$thumb;InstallLocation=$app.InstallLocation;PackageFamilyName=$app.PackageFamilyName}
 $lockLease.Dispose();$lockLease=$null
 & $nextCli update-restart $projectPath (Split-Path $stateRoot -Parent)|Out-Null;if($LASTEXITCODE -ne 0){throw 'new packaged runtime restart failed'}
 $newHealth=$null
 for($n=0;$n -lt 30;$n++){try{$newHealth=& $nextCli update-health $projectPath 2>$null|ConvertFrom-Json;if($LASTEXITCODE -eq 0 -and $newHealth){break}}catch{};Start-Sleep -Seconds 1}
 if(!$newHealth -or $newHealth.Version -ne 1 -or $newHealth.StateSchema -ne 3 -or $newHealth.AssemblySha256 -ne $spec.AppBinarySha256){throw 'new owning runtime health handshake failed'}
 $record.Status='Succeeded';$record.CompletedUtc=[DateTime]::UtcNow.ToString('o');SaveJson $pendingPath $record
 'Signed update installed and new CLI handshake confirmed.'
}catch{
 $record.Status=if($installAttempted){'RecoveryRequired'}else{'Aborted'};$record.Error=$_.Exception.Message
 $safe=$false
 if(!$lockLease){try{$lockLease=[IO.File]::Open($lock,'Open','ReadWrite','None')}catch{$record.Error+='; project ownership resumed before rollback'}}
 try{if($lockLease -and $statePath -and (Test-Path $statePath -PathType Leaf)){$saved=Get-Content $statePath -Raw|ConvertFrom-Json;$safe=($saved.Generation -eq $generation -and $null -ne $saved.Schema -and [int]$saved.Schema -le [int]$schema -and ((StateRefs $stateRoot) -join "`n") -eq ($refs -join "`n") -and !(Get-Process AgentOS,AgentOS.Cli -ErrorAction SilentlyContinue))}}catch{$record.Error+='; rollback eligibility unavailable: '+$_.Exception.Message}
 if($safe -and $installAttempted){try{Signed (Join-Path $backup 'prior.msix') $thumb;if((Get-FileHash (Join-Path $backup 'prior.msix') -Algorithm SHA256).Hash -ne $old.Sha256){throw 'rollback package changed'};$priorLease=[IO.File]::Open((Join-Path $backup 'prior.msix'),'Open','Read','Read');try{if((Get-FileHash (Join-Path $backup 'prior.msix') -Algorithm SHA256).Hash -ne $old.Sha256){throw 'rollback package changed under lease'};Add-AppxPackage -Path (Join-Path $backup 'prior.msix') -ForceUpdateFromAnyVersion -ErrorAction Stop}finally{$priorLease.Dispose()};$restored=Get-AppxPackage -Name $id.Name|Where-Object {$_.Version.ToString() -eq $old.Version -and $_.Publisher -eq $old.Publisher}|Select-Object -First 1;if(!$restored){throw 'prior signed package not restored'};$restoredMetadata=Get-Content (Join-Path $backup 'prior.json') -Raw|ConvertFrom-Json;$restoredMetadata.PackageFile=Join-Path $backup 'prior.msix';SaveJson $current $restoredMetadata;$record.Status='RolledBack'}catch{$record.Error+='; rollback failed: '+$_.Exception.Message}}
 SaveJson $pendingPath $record
 throw "Update $($record.Status): $($record.Error)"
}finally{if($lockLease){$lockLease.Dispose()}}

