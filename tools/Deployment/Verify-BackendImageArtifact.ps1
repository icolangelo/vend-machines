[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$ExpectedSourceSha
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$resolvedArtifactPath = (Resolve-Path -LiteralPath $ArtifactPath -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $resolvedArtifactPath -PathType Container)) {
    throw "Informe o diretório extraído do artefato de imagem backend."
}

$requiredFiles = @(
    "backend-image.tar.gz",
    "image-id.txt",
    "image-ref.txt",
    "platform.txt",
    "source-sha.txt",
    "README.txt",
    "Verify-BackendImageArtifact.ps1",
    "start-private-backend-candidate.sh"
)
$expectedDirectoryEntries = @($requiredFiles + "SHA256SUMS" | Sort-Object)
$actualDirectoryEntries = @(Get-ChildItem -LiteralPath $resolvedArtifactPath -Force | Sort-Object Name)
if ($actualDirectoryEntries.Count -ne $expectedDirectoryEntries.Count) {
    throw "O diretório contém arquivos extras ou incompletos; imagem recusada."
}
for ($index = 0; $index -lt $expectedDirectoryEntries.Count; $index++) {
    $entry = $actualDirectoryEntries[$index]
    if ($entry.Name -cne $expectedDirectoryEntries[$index] -or
        $entry.PSIsContainer -or
        ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "O diretório do artefato contém caminho inesperado; imagem recusada."
    }
}

$checksumsPath = Join-Path $resolvedArtifactPath "SHA256SUMS"
if (-not (Test-Path -LiteralPath $checksumsPath -PathType Leaf)) {
    throw "O artefato não contém SHA256SUMS; imagem recusada."
}

$checksumEntries = @{}
foreach ($line in Get-Content -LiteralPath $checksumsPath) {
    if ($line -notmatch '^([A-Fa-f0-9]{64})  ([^\\/\s]+)$') {
        throw "Entrada inválida em SHA256SUMS; imagem recusada."
    }

    $expectedHash = $matches[1]
    $fileName = $matches[2]
    if ($checksumEntries.ContainsKey($fileName)) {
        throw "Nome repetido em SHA256SUMS; imagem recusada."
    }

    $filePath = Join-Path $resolvedArtifactPath $fileName
    $file = Get-Item -LiteralPath $filePath -ErrorAction Stop
    if ($file.Length -le 0 -or $file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "O artefato contém caminho não regular; imagem recusada."
    }

    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $filePath).Hash
    if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Checksum inválido para $fileName; imagem recusada."
    }
    $checksumEntries[$fileName] = $true
}

if ($checksumEntries.Count -ne $requiredFiles.Count) {
    throw "SHA256SUMS precisa cobrir exatamente os arquivos esperados; imagem recusada."
}
foreach ($fileName in $requiredFiles) {
    if (-not $checksumEntries.ContainsKey($fileName)) {
        throw "Arquivo obrigatório ausente de SHA256SUMS: $fileName."
    }
}

$sourceSha = (Get-Content -LiteralPath (Join-Path $resolvedArtifactPath "source-sha.txt") -Raw).Trim()
if (-not [string]::Equals($sourceSha, $ExpectedSourceSha, [StringComparison]::OrdinalIgnoreCase)) {
    throw "O SHA de origem do artefato diverge do SHA esperado; imagem recusada."
}

$imageRef = (Get-Content -LiteralPath (Join-Path $resolvedArtifactPath "image-ref.txt") -Raw).Trim()
$expectedImageRef = "vend-machines-backend:$ExpectedSourceSha"
if (-not [string]::Equals($imageRef, $expectedImageRef, [StringComparison]::OrdinalIgnoreCase)) {
    throw "A referência da imagem não está associada ao SHA esperado; imagem recusada."
}

$imageId = (Get-Content -LiteralPath (Join-Path $resolvedArtifactPath "image-id.txt") -Raw).Trim()
if ($imageId -notmatch '^sha256:[A-Fa-f0-9]{64}$') {
    throw "O ID da imagem está ausente ou malformado; imagem recusada."
}

$artifactPlatform = (Get-Content -LiteralPath (Join-Path $resolvedArtifactPath "platform.txt") -Raw).Trim().ToLowerInvariant()
if ($artifactPlatform -notmatch '^linux/(amd64|arm64)$') {
    throw "Plataforma de imagem não suportada pelo artefato: $artifactPlatform."
}

$daemonPlatformOutput = & docker info --format '{{.OSType}}/{{.Architecture}}' 2>$null
if ((Test-Path Variable:LASTEXITCODE) -and $LASTEXITCODE -ne 0) {
    throw "Não foi possível consultar a plataforma do daemon Docker; imagem recusada."
}
$daemonPlatform = ([string]$daemonPlatformOutput).Trim().ToLowerInvariant()
$daemonPlatform = $daemonPlatform.Replace("linux/x86_64", "linux/amd64").Replace("linux/aarch64", "linux/arm64")
if (-not [string]::Equals($artifactPlatform, $daemonPlatform, [StringComparison]::Ordinal)) {
    throw "A plataforma da imagem ($artifactPlatform) não corresponde ao daemon ($daemonPlatform); imagem recusada."
}

Write-Output "Artefato validado: $imageRef ($artifactPlatform, $imageId). A imagem ainda não foi carregada nem iniciada."
