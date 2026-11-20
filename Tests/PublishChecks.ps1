$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$env:TORCH_API_KEY = 'test-only-publishing-secret'
$fixtureZip = Join-Path ([System.IO.Path]::GetTempPath()) "torch-publish-$([guid]::NewGuid()).zip"
Set-Content $fixtureZip 'fixture'
[xml]$manifest = Get-Content manifest.xml
try {
    $global:ExistingRegistration = @{}
    $global:ExpectedAction = 'upload'
    $global:Uploads = 0
    function Invoke-RestMethod {
        param($Uri, $Method, $Body, $TimeoutSec)
        if (!$Method) { return $global:ExistingRegistration }
        $Form = @{}
        foreach ($part in $Body) {
            $name = $part.Headers.ContentDisposition.Name.Trim('"')
            $Form[$name] = $part.ReadAsStringAsync().GetAwaiter().GetResult()
            if ($name -eq 'uploadZip' -and ($part.Headers.ContentType.MediaType -ne 'application/zip' -or !$part.Headers.ContentDisposition.FileName)) { throw 'ZIP MIME type or filename missing.' }
        }
        if ($Uri -ne 'https://torchapi.com/api/plugins/upload' -or $Method -ne 'Post' -or $Form.username -ne 'OwendB' -or $Form.secret -ne $env:TORCH_API_KEY -or $Form.action -ne $global:ExpectedAction -or !$Form.uploadZip) {
            throw 'Unexpected upload contract.'
        }
        if ($Form.action -eq 'upload' -and (!$Form.name -or !$Form.description -or $Form.isPrivate -ne 'false')) { throw 'Missing first-upload metadata.' }
        if ($Form.action -eq 'update' -and !$Form.updateNote) { throw 'Missing update note.' }
        $global:Uploads++
        return '{"success_message":"fixture uploaded"}'
    }
    & ./ci/Publish-Torch.ps1 -ZipPath $fixtureZip
    $global:ExistingRegistration = @{ guid = $manifest.PluginManifest.Guid; latestVersion = '0.0.0'; versions = @(@{ version = '0.0.0' }) }
    $global:ExpectedAction = 'update'
    & ./ci/Publish-Torch.ps1 -ZipPath $fixtureZip
    if ($global:Uploads -ne 2) { throw 'Expected initial upload and update.' }
    $global:ExistingRegistration.versions += @{ version = "v$($manifest.PluginManifest.Version)" }
    & ./ci/Publish-Torch.ps1 -ZipPath $fixtureZip
    if ($global:Uploads -ne 2) { throw 'Retry uploaded a version already published to Torch.' }
    $global:ExistingRegistration.versions = @(@{ version = '0.0.0' })
    $global:ExistingRegistration.latestVersion = '9.0.0'
    try { & ./ci/Publish-Torch.ps1 -ZipPath $fixtureZip; throw 'Downgrade was accepted.' }
    catch { if ($_.Exception.Message -notlike '*older version*') { throw } }
    Write-Host 'PASS native multipart upload/update contract, retry and downgrade guards'
} finally { Remove-Item $fixtureZip; Remove-Item Env:TORCH_API_KEY }
