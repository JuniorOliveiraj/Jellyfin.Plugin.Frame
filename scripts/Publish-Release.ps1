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
$assets = @($zip, "$zip.sha256", $manifestPath)
foreach ($asset in $assets) {
    if (!(Test-Path -LiteralPath $asset -PathType Leaf)) { throw "Arquivo ausente: $asset" }
}

function Invoke-GitHub([string[]] $Arguments) {
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        $output = & gh @Arguments 2>&1
        if ($LASTEXITCODE -eq 0) { return $output }
        $message = $output -join [Environment]::NewLine
        if ($attempt -eq 4 -or $message -notmatch 'HTTP (429|5\d{2})\b|stream error:.*(?:CANCEL|REFUSED_STREAM)|connection reset by peer|unexpected EOF|TLS handshake timeout') {
            throw $message
        }
        Write-Warning "GitHub respondeu com erro transitório. Tentativa $attempt de 4."
        Start-Sleep -Seconds ([math]::Pow(2, $attempt))
        if ($Arguments[0] -eq 'release' -and $Arguments[1] -eq 'create') {
            # The create may have succeeded remotely. Let the caller inspect the release before retrying.
            return $null
        }
    }
}

$releaseFields = '.[] | {id,tag_name,draft,target_commitish} | @json'
for ($attempt = 1; $attempt -le 4; $attempt++) {
    $releases = @(Invoke-GitHub @('api', '--paginate', "repos/$Repository/releases", '--jq', $releaseFields) |
        ForEach-Object { $_ | ConvertFrom-Json })
    foreach ($published in $releases) {
        if (!$published.draft -and $published.tag_name -match '^v(\d+\.\d+\.\d+\.\d+)$' -and
            [version] $Matches[1] -ge $version) {
            Write-Output "A versão $($published.tag_name) já está publicada. Nenhuma release criada."
            return
        }
    }

    $tagFilter = '.[] | select(.ref == "refs/tags/' + $tag + '") | .object.sha'
    $remoteTag = Invoke-GitHub @('api', '--paginate', "repos/$Repository/git/matching-refs/tags/$tag", '--jq', $tagFilter)
    if ($remoteTag -and $remoteTag -ne $Commit) {
        throw "A tag $tag já existe e aponta para outro commit."
    }

    $draft = $releases | Where-Object { $_.draft -and $_.tag_name -eq $tag } | Select-Object -First 1
    if (!$draft) {
        $created = Invoke-GitHub @('release', 'create', $tag, '--draft', '--repo', $Repository, '--target', $Commit,
            '--title', "Frame $version", '--notes', "Jellyfin.Plugin.Frame $version")
        if (!$created) { continue }
    } elseif (!$remoteTag -and $draft.target_commitish -ne $Commit) {
        $previous = $draft.target_commitish
        if ($previous -notmatch '^[0-9a-f]{40}$' -or $draft.id -isnot [long] -and $draft.id -isnot [int]) {
            throw "Não foi possível verificar o commit do rascunho $tag."
        }
        git -C $root merge-base --is-ancestor $previous $Commit
        if ($LASTEXITCODE -ne 0) {
            throw "O commit do rascunho $tag não está no histórico do commit atual da main."
        }
        $updated = Invoke-GitHub @('api', '--method', 'PATCH', "repos/$Repository/releases/$($draft.id)",
            '-f', "target_commitish=$Commit", '--jq', '.target_commitish')
        if ($updated -ne $Commit) {
            throw "O GitHub não confirmou a atualização do rascunho $tag."
        }
    }

    Invoke-GitHub (@('release', 'upload', $tag) + $assets + @('--repo', $Repository, '--clobber')) | Write-Output
    Invoke-GitHub @('release', 'edit', $tag, '--repo', $Repository, '--draft=false', '--latest') | Write-Output
    return
}
throw "O GitHub não confirmou a criação de $tag após quatro tentativas."
