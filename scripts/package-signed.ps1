param(
 [Parameter(Mandatory)][string]$ArtifactsOutput,
 [string]$CertificateThumbprint,
 [string]$Publisher,
 [string]$Version='1.0.0.0',
 [string]$MakeAppx,
 [string]$SignTool,
 [string]$DotnetPath,
 [switch]$UnsignedDiagnostic
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$out=[IO.Path]::GetFullPath($ArtifactsOutput).TrimEnd('\')
$repo=[IO.Path]::GetFullPath($repo).TrimEnd('\')
$cwd=[IO.Path]::GetFullPath([Environment]::CurrentDirectory).TrimEnd('\')
$running=[IO.Path]::GetFullPath((Get-Process -Id $PID).Path)
function Overlap($a,$b){return $a -eq $b -or $a.StartsWith($b+'\',[StringComparison]::OrdinalIgnoreCase) -or $b.StartsWith($a+'\',[StringComparison]::OrdinalIgnoreCase)}
if($out -eq [IO.Path]::GetPathRoot($out).TrimEnd('\') -or (Overlap $out $repo) -or (Overlap $out $cwd) -or $running.StartsWith($out+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'ArtifactsOutput overlaps root, repository, workspace or running installation.'}
for($cursor=[IO.DirectoryInfo]::new($out);$cursor;$cursor=$cursor.Parent){if($cursor.Exists -and ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'ArtifactsOutput traverses a reparse point.'}}
if($Version -notmatch '^\d+\.\d+\.\d+\.\d+$'){throw 'Version must have four numeric components.'}
$sdk=$DotnetPath; if(!$sdk){$sdk=Join-Path $repo '.tools/dotnet/dotnet.exe'}
if(!(Test-Path $sdk) -and !$DotnetPath){$sdk='C:/Program Files/dotnet/dotnet.exe'}
if(!(Test-Path $sdk)){throw '.NET SDK unavailable.'}
if((& $sdk --version | Select-Object -First 1) -ne '10.0.401'){throw '.NET SDK 10.0.401 required.'}
$kit='C:/Program Files (x86)/Windows Kits/10/bin'
if(!$MakeAppx){$MakeAppx=Get-ChildItem $kit -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue | Where-Object FullName -Match 'x64' | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName}
if(!$SignTool){$SignTool=Get-ChildItem $kit -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue | Where-Object FullName -Match 'x64' | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName}
if(!$MakeAppx -or !(Test-Path $MakeAppx)){throw 'Windows SDK MakeAppx unavailable.'}
$cert=$null
if(!$UnsignedDiagnostic){
 if(!$CertificateThumbprint){throw 'Signed package requires CertificateThumbprint.'}
 $cert=Get-ChildItem Cert:\CurrentUser\My,Cert:\LocalMachine\My -ErrorAction SilentlyContinue | Where-Object Thumbprint -EQ ($CertificateThumbprint -replace '\s','') | Select-Object -First 1
 if(!$cert -or !$cert.HasPrivateKey){throw 'Signing certificate or private key unavailable.'}
 if(!$Publisher){$Publisher=$cert.Subject}
 if($Publisher -cne $cert.Subject){throw 'Publisher must exactly match signing certificate subject.'}
 if($cert.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow -or $cert.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow){throw 'Signing certificate expired or not yet valid.'}
 $eku=$cert.Extensions|Where-Object {$_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]}|Select-Object -First 1
 if(!$eku -or !($eku.EnhancedKeyUsages|Where-Object Value -eq '1.3.6.1.5.5.7.3.3')){throw 'Signing certificate lacks Code Signing EKU.'}
 $chain=[Security.Cryptography.X509Certificates.X509Chain]::new();try{if(!$chain.Build($cert)){throw 'Signing certificate chain is not trusted.'}}finally{$chain.Dispose()}
 if(!$SignTool -or !(Test-Path $SignTool)){throw 'Windows SDK SignTool unavailable.'}
}else{
 if(!$Publisher){$Publisher='CN=AgentOS Unsigned Diagnostic'}
}
if(!(Get-Command Get-AppxPackage -ErrorAction SilentlyContinue)){throw 'Installed MSIX inspection unavailable; output path cannot be checked.'}
try{$installed=Get-AppxPackage -Name 'AgentOS.Desktop' -ErrorAction Stop|Select-Object -ExpandProperty InstallLocation}catch{throw 'Installed MSIX inspection unavailable; output path cannot be checked.'}
foreach($place in $installed){$place=[IO.Path]::GetFullPath($place).TrimEnd('\');if(Overlap $out $place){throw 'ArtifactsOutput overlaps installed AgentOS runtime.'}}
New-Item -ItemType Directory -Force $out | Out-Null
$layout=Join-Path $out 'layout'
if(Test-Path $layout){$item=Get-Item -LiteralPath $layout -Force;if(!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Layout is not a plain owned directory.'};if(Get-ChildItem -LiteralPath $layout -Recurse -Force|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}){throw 'Layout contains a linked path.'};Remove-Item -LiteralPath $layout -Recurse -Force}
$app=Join-Path $layout 'app'
$cli=Join-Path $app 'cli'
& $sdk publish (Join-Path $repo 'src/AgentOS.App/AgentOS.App.csproj') -c Release -r win-x64 --self-contained true -o $app -v minimal
if($LASTEXITCODE -ne 0){throw 'App publication failed.'}
& $sdk publish (Join-Path $repo 'src/AgentOS.Cli/AgentOS.Cli.csproj') -c Release -r win-x64 --self-contained true -o $cli -v minimal
if($LASTEXITCODE -ne 0){throw 'CLI publication failed.'}
foreach($name in 'AgentOS.exe','AgentOS.dll','AgentOS.Core.dll','App.xbf','AgentOS.pri','hostfxr.dll'){
 if(!(Test-Path (Join-Path $app $name))){throw "Required app artifact missing: $name"}
}
foreach($name in 'AgentOS.Cli.exe','AgentOS.Core.dll'){if(!(Test-Path (Join-Path $cli $name))){throw "Required CLI artifact missing: $name"}}
$protocol=@{Protocol=1;StateSchema=2;SourceSha256=(Get-FileHash (Join-Path $repo 'src/AgentOS.Core/RuntimeUpdate.cs') -Algorithm SHA256).Hash;BinarySha256=(Get-FileHash (Join-Path $cli 'AgentOS.Core.dll') -Algorithm SHA256).Hash;AppBinarySha256=(Get-FileHash (Join-Path $app 'AgentOS.Core.dll') -Algorithm SHA256).Hash}
$protocol|ConvertTo-Json|Set-Content (Join-Path $app 'update-protocol.json') -Encoding utf8
$manifest=Get-Content (Join-Path $repo 'packaging/AppxManifest.xml.in') -Raw
$manifest=$manifest.Replace('{{PUBLISHER}}',[Security.SecurityElement]::Escape($Publisher)).Replace('{{VERSION}}',$Version)
$manifest | Set-Content (Join-Path $app 'AppxManifest.xml') -Encoding utf8
$assets=Join-Path $app 'Assets'; New-Item -ItemType Directory -Force $assets | Out-Null
Add-Type -AssemblyName System.Drawing
foreach($size in 44,50,150){
 $bmp=[Drawing.Bitmap]::new($size,$size)
 try{
  $g=[Drawing.Graphics]::FromImage($bmp)
  try{$g.Clear([Drawing.Color]::FromArgb(22,41,69))}finally{$g.Dispose()}
  $bmp.Save((Join-Path $assets "Square${size}x${size}Logo.png"),[Drawing.Imaging.ImageFormat]::Png)
 }finally{$bmp.Dispose()}
}
Copy-Item (Join-Path $assets 'Square44x44Logo.png') (Join-Path $assets 'StoreLogo.png')
$package=Join-Path $out $(if($UnsignedDiagnostic){'AgentOS-UNSIGNED-DIAGNOSTIC.msix'}else{'AgentOS.msix'})
foreach($file in @($package,(Join-Path $out 'package-manifest.json'))){if(Test-Path -LiteralPath $file){$item=Get-Item -LiteralPath $file -Force;if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Output contains a linked or nonfile artifact.'}}}
& $MakeAppx pack /d $app /p $package /o
if($LASTEXITCODE -ne 0){throw 'MSIX packaging failed.'}
if(!$UnsignedDiagnostic){
 $signArgs=@('sign','/fd','SHA256','/sha1',$cert.Thumbprint);if($cert.PSParentPath -match 'LocalMachine'){$signArgs+='/sm'};$signArgs+=$package
 & $SignTool @signArgs
 if($LASTEXITCODE -ne 0){throw 'MSIX signing failed.'}
 $signature=Get-AuthenticodeSignature $package
 if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $cert.Thumbprint){throw 'Signed MSIX verification failed.'}
}
$hash=(Get-FileHash $package -Algorithm SHA256).Hash
@{Schema=1;Protocol=1;StateSchema=2;SourceSha256=$protocol.SourceSha256;BinarySha256=$protocol.BinarySha256;AppBinarySha256=$protocol.AppBinarySha256;IdentityName='AgentOS.Desktop';Publisher=$Publisher;Version=$Version;Package=(Split-Path $package -Leaf);Sha256=$hash;SignerThumbprint=$(if($cert){$cert.Thumbprint}else{''});Diagnostic=[bool]$UnsignedDiagnostic} |
 ConvertTo-Json | Set-Content (Join-Path $out 'package-manifest.json') -Encoding utf8
Write-Output $package






