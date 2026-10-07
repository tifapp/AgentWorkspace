param(
 [Parameter(Mandatory)][string]$Package,
 [Parameter(Mandatory)][string]$Manifest,
 [Parameter(Mandatory)][string]$InstallRoot,
 [Parameter(Mandatory)][string]$TrustedSignerThumbprint
)
$ErrorActionPreference='Stop'
$packagePath=[IO.Path]::GetFullPath($Package)
$manifestPath=[IO.Path]::GetFullPath($Manifest)
$installPath=[IO.Path]::GetFullPath($InstallRoot)
if(!(Test-Path $packagePath -PathType Leaf) -or !(Test-Path $manifestPath -PathType Leaf)){throw 'Package or manifest missing.'}
$spec=Get-Content $manifestPath -Raw | ConvertFrom-Json
if($spec.Schema -ne 1 -or $spec.Diagnostic -or $spec.Package -ne [IO.Path]::GetFileName($packagePath)){throw 'Package manifest mismatch or unsigned diagnostic package.'}
if((Get-FileHash $packagePath -Algorithm SHA256).Hash -ne $spec.Sha256){throw 'Package hash mismatch.'}
$expected=$TrustedSignerThumbprint -replace '\s',''
if($spec.SignerThumbprint -ne $expected){throw 'Manifest signer differs from trusted signer.'}
$sig=Get-AuthenticodeSignature -LiteralPath $packagePath
if($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Thumbprint -ne $expected){throw 'Package signature is invalid or signer is untrusted.'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead($packagePath)
try{
 $entry=$archive.GetEntry('AppxManifest.xml')
 if(!$entry){throw 'MSIX identity manifest missing.'}
 $reader=[IO.StreamReader]::new($entry.Open())
 try{[xml]$xml=$reader.ReadToEnd()}finally{$reader.Dispose()}
 if($xml.Package.Identity.Version -ne $spec.Version -or $xml.Package.Identity.ProcessorArchitecture -ne 'x64' -or ![Environment]::Is64BitOperatingSystem){throw 'MSIX version or architecture mismatch.'}
 if([version]$xml.Package.Dependencies.TargetDeviceFamily.MinVersion -gt [Environment]::OSVersion.Version){throw 'Host OS is below package minimum.'}
}finally{$archive.Dispose()}
if(!(Test-Path $installPath -PathType Container)){throw 'Install root is missing.'}
$current=Join-Path $installPath 'package-manifest.json'
if(Test-Path $current){$installed=Get-Content $current -Raw | ConvertFrom-Json; if([version]$spec.Version -le [version]$installed.Version){throw 'Update version must be newer than installed version.'}}
$state=Join-Path $installPath 'update-state'
New-Item -ItemType Directory -Force $state | Out-Null
if(Test-Path $current){Copy-Item -LiteralPath $current -Destination (Join-Path $state ('installed.backup.'+[DateTime]::UtcNow.ToString('yyyyMMddHHmmss')+'.json'))}
$pending=Join-Path $state 'pending.msix'
Copy-Item -LiteralPath $packagePath -Destination ($pending+'.next') -Force
if((Get-FileHash ($pending+'.next') -Algorithm SHA256).Hash -ne $spec.Sha256){throw 'Staged package hash mismatch.'}
Move-Item -LiteralPath ($pending+'.next') -Destination $pending -Force
$existing=Join-Path $state 'pending.json'
if(Test-Path $existing){Copy-Item $existing (Join-Path $state ('pending.backup.'+[DateTime]::UtcNow.ToString('yyyyMMddHHmmss')+'.json'))}
$record=@{Schema=1;Status='PendingDrainAndVersionHandshake';PackageSha256=$spec.Sha256;Version=$spec.Version;Signer=$expected;CreatedUtc=[DateTime]::UtcNow.ToString('o');PreviousVersion=$(if($installed){$installed.Version}else{$null});RollbackPackage=$null}
$record | ConvertTo-Json | Set-Content ($existing+'.next') -Encoding utf8
Move-Item ($existing+'.next') $existing -Force
Write-Output 'Verified package staged. Installation remains pending because no task drain and reversible version handshake API is available.'




