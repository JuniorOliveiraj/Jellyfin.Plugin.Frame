param(
    [Parameter(Mandatory)]
    [string] $Repository,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $Commit
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$release = Join-Path $root 'artifacts/release'
$manifestPath = Join-Path $release 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -NoEnumerate
$version = [version] $manifest[0].versions[0].version
$tag = "v$version"
$zip = Join-Path $release "Jellyfin.Plugin.Frame-$version.zip"
$shaFile = "$zip.sha256"
foreach ($asset in @($zip, $shaFile)) {
    if (!(Test-Path -LiteralPath $asset -PathType Leaf)) { throw "Arquivo ausente: $asset" }
}

$published = gh api --paginate "repos/$Repository/releases" --jq '.[] | select(.draft == false) | .tag_name'
if ($LASTEXITCODE -ne 0) { throw 'Falha ao consultar as releases existentes.' }
foreach ($publishedTag in $published) {
    if ($publishedTag -match '^v(\d+\.\d+\.\d+\.\d+)$' -and [version] $Matches[1] -ge $version) {
        Write-Output "A versão $publishedTag já está publicada. Nenhuma release criada."
        return
    }
}

$remoteTag = gh api --paginate "repos/$Repository/git/matching-refs/tags/$tag" --jq ".[] | select(.ref == `"refs/tags/$tag`") | .object.sha"
if ($LASTEXITCODE -ne 0) { throw 'Falha ao consultar a tag da release.' }
if ($remoteTag -and $remoteTag -ne $Commit) {
    throw "A tag $tag já existe e aponta para outro commit."
}

gh release create $tag $zip $shaFile $manifestPath --repo $Repository --target $Commit --generate-notes --latest
if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar $tag." }
