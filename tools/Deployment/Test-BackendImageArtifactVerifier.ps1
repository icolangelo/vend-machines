$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$verifierPath = Join-Path $repositoryRoot "tools/Deployment/Verify-BackendImageArtifact.ps1"
$temporaryPath = Join-Path ([IO.Path]::GetTempPath()) "vendmachines-image-artifact-$([Guid]::NewGuid().ToString('N'))"
$sourceSha = "a" * 40

function Write-TestManifest([string]$Directory) {
    $manifestEntries = Get-ChildItem -LiteralPath $Directory -File |
        Where-Object { $_.Name -ne "SHA256SUMS" } |
        Sort-Object Name |
        ForEach-Object {
            $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
            "$hash  $($_.Name)"
        }
    [IO.File]::WriteAllLines((Join-Path $Directory "SHA256SUMS"), [string[]]$manifestEntries, [Text.Encoding]::ASCII)
}

function Assert-Rejected([scriptblock]$Action, [string]$Scenario) {
    $rejected = $false
    try {
        & $Action | Out-Null
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw "O verificador aceitou um artefato inválido: $Scenario."
    }
}

function docker {
    "linux/x86_64"
}

New-Item -ItemType Directory -Path $temporaryPath | Out-Null
try {
    [IO.File]::WriteAllBytes((Join-Path $temporaryPath "backend-image.tar.gz"), [byte[]](1, 2, 3, 4, 5))
    [IO.File]::WriteAllText((Join-Path $temporaryPath "image-id.txt"), "sha256:$('b' * 64)`n")
    [IO.File]::WriteAllText((Join-Path $temporaryPath "image-ref.txt"), "vend-machines-backend:$sourceSha`n")
    [IO.File]::WriteAllText((Join-Path $temporaryPath "platform.txt"), "linux/amd64`n")
    [IO.File]::WriteAllText((Join-Path $temporaryPath "source-sha.txt"), "$sourceSha`n")
    [IO.File]::WriteAllText((Join-Path $temporaryPath "README.txt"), "Fixture de teste.`n")
    Copy-Item -LiteralPath $verifierPath -Destination (Join-Path $temporaryPath "Verify-BackendImageArtifact.ps1")
    Copy-Item -LiteralPath (Join-Path $repositoryRoot "tools/Deployment/start-private-backend-candidate.sh") -Destination (Join-Path $temporaryPath "start-private-backend-candidate.sh")
    Copy-Item -LiteralPath (Join-Path $repositoryRoot "tools/Deployment/verify-candidate-artifact-pair.py") -Destination (Join-Path $temporaryPath "verify-candidate-artifact-pair.py")
    Write-TestManifest $temporaryPath

    & $verifierPath -ArtifactPath $temporaryPath -ExpectedSourceSha $sourceSha | Out-Null

    Assert-Rejected {
        & $verifierPath -ArtifactPath $temporaryPath -ExpectedSourceSha ("c" * 40)
    } "SHA de origem divergente"

    [IO.File]::WriteAllBytes((Join-Path $temporaryPath "backend-image.tar.gz"), [byte[]](5, 4, 3, 2, 1))
    Assert-Rejected {
        & $verifierPath -ArtifactPath $temporaryPath -ExpectedSourceSha $sourceSha
    } "checksum divergente"

    [IO.File]::WriteAllBytes((Join-Path $temporaryPath "backend-image.tar.gz"), [byte[]](1, 2, 3, 4, 5))
    [IO.File]::WriteAllText((Join-Path $temporaryPath "platform.txt"), "linux/arm64`n")
    Write-TestManifest $temporaryPath
    Assert-Rejected {
        & $verifierPath -ArtifactPath $temporaryPath -ExpectedSourceSha $sourceSha
    } "plataforma diferente do daemon"

    [IO.File]::WriteAllText((Join-Path $temporaryPath "platform.txt"), "linux/amd64`n")
    Write-TestManifest $temporaryPath
    $manifestPath = Join-Path $temporaryPath "SHA256SUMS"
    Add-Content -LiteralPath $manifestPath -Value ("$([string]::new('d', 64))  ..\outside.txt")
    Assert-Rejected {
        & $verifierPath -ArtifactPath $temporaryPath -ExpectedSourceSha $sourceSha
    } "caminho fora do diretório do artefato"

    Write-Output "Backend image artifact verifier: 4 cenários passaram."
}
finally {
    Remove-Item -LiteralPath $temporaryPath -Recurse -Force -ErrorAction SilentlyContinue
}
