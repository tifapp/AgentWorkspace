param([string]$Output = 'artifacts/verification/source-manifest.json')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repo
try {
 $paths=@(& git ls-files --cached --others --exclude-standard) | Sort-Object -Unique
 $files=@(foreach($path in $paths) { if(Test-Path -LiteralPath $path -PathType Leaf) { [ordered]@{path=$path;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash} } })
 $payload=$files | ConvertTo-Json -Depth 5 -Compress
 $sha=[Security.Cryptography.SHA256]::Create()
 $digest=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($payload))).Replace('-','')
 $sha.Dispose()
 $manifest=[ordered]@{capturedAt=[DateTimeOffset]::UtcNow.ToString('o');head=(& git rev-parse HEAD);sourceSha256=$digest;os=[Environment]::OSVersion.ToString();files=$files}
 $absolute=[IO.Path]::GetFullPath((Join-Path $repo $Output))
 [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($absolute)) | Out-Null
 $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $absolute -Encoding UTF8
 Write-Output "Source manifest: $absolute ($digest)"
} finally { Pop-Location }
