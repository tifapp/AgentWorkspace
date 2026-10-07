param(
 [Parameter(Mandatory)][string]$ArtifactsOutput,
 [string]$CertificateThumbprint,
 [string]$Publisher,
 [string]$Version='1.0.0.0',
 [string]$MakeAppx,
 [string]$SignTool,
 [switch]$UnsignedDiagnostic
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$out=[IO.Path]::GetFullPath($ArtifactsOutput)
if($Version -notmatch '^\d+\.\d+\.\d+\.\d+$'){throw 'Version must have four numeric components.'}
$sdk=Join-Path $repo '.tools/dotnet/dotnet.exe'
if(!(Test-Path $sdk)){$sdk='C:/Program Files/dotnet/dotnet.exe'}
if(!(Test-Path $sdk)){throw '.NET SDK unavailable.'}
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
 if($Publisher -ne $cert.Subject){throw 'Publisher must exactly match signing certificate subject.'}
 if(!$SignTool -or !(Test-Path $SignTool)){throw 'Windows SDK SignTool unavailable.'}
}else{
 if(!$Publisher){$Publisher='CN=AgentOS Unsigned Diagnostic'}
}
New-Item -ItemType Directory -Force $out | Out-Null
$layout=Join-Path $out 'layout'
if(Test-Path $layout){Remove-Item -LiteralPath $layout -Recurse -Force}
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
& $MakeAppx pack /d $app /p $package /o
if($LASTEXITCODE -ne 0){throw 'MSIX packaging failed.'}
if(!$UnsignedDiagnostic){
 & $SignTool sign /fd SHA256 /sha1 ($cert.Thumbprint) $package
 if($LASTEXITCODE -ne 0){throw 'MSIX signing failed.'}
 $signature=Get-AuthenticodeSignature $package
 if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $cert.Thumbprint){throw 'Signed MSIX verification failed.'}
}
$hash=(Get-FileHash $package -Algorithm SHA256).Hash
@{Schema=1;Version=$Version;Package=(Split-Path $package -Leaf);Sha256=$hash;SignerThumbprint=$(if($cert){$cert.Thumbprint}else{''});Diagnostic=[bool]$UnsignedDiagnostic} |
 ConvertTo-Json | Set-Content (Join-Path $out 'package-manifest.json') -Encoding utf8
Write-Output $package




