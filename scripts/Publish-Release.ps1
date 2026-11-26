param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string] $Repository,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $Commit,

    [string] $ReleaseDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/release')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { throw 'GH_TOKEN não configurado.' }
$headers = @{
    Authorization = "Bearer $env:GH_TOKEN"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
}
$api = "https://api.github.com/repos/$Repository"
$uploads = "https://uploads.github.com/repos/$Repository"
$manifestPath = Join-Path $ReleaseDirectory 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -NoEnumerate
$entries = @($manifest[0].versions)
if ($manifest.Count -ne 1 -or $manifest[0].guid -ne 'bb053bff-fcf8-4e86-b3b6-83b83866063e' -or
    $entries.Count -ne 2 -or [version] $entries[0].version -le [version] $entries[1].version -or
    [version] $entries[0].targetAbi -le [version] $entries[1].targetAbi) {
    throw 'O manifesto precisa listar os dois pacotes do Frame, com a versão mais recente primeiro.'
}
$version = [version] $entries[0].version
$tag = "v$version"
$packagePaths = @(foreach ($entry in $entries) {
    $packageVersion = [version] $entry.version
    $zipName = "Jellyfin.Plugin.Frame-$packageVersion.zip"
    $zip = Join-Path $ReleaseDirectory $zipName
    $shaFile = "$zip.sha256"
    $expectedUrl = "https://github.com/$Repository/releases/download/$tag/$zipName"
    if ($entry.sourceUrl -cne $expectedUrl -or $entry.checksum -ine (Get-FileHash -LiteralPath $zip -Algorithm MD5).Hash) {
        throw "O manifesto não corresponde ao ZIP $zipName."
    }
    $sha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ((Get-Content -LiteralPath $shaFile -Raw).Trim() -cne "$sha256  $zipName") {
        throw "O arquivo SHA-256 não corresponde ao ZIP $zipName."
    }
    $zip
    $shaFile
})
$files = foreach ($path in ($packagePaths + $manifestPath)) {
    $file = Get-Item -LiteralPath $path
    [pscustomobject] @{
        Name = $file.Name
        Path = $file.FullName
        Size = $file.Length
        Digest = 'sha256:' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Stop-Transient([string] $Message) {
    $failure = [InvalidOperationException]::new($Message)
    $failure.Data['GitHubTransient'] = $true
    throw $failure
}

function Invoke-GitHub {
    param(
        [string] $Operation,
        [string] $Method,
        [string] $Uri,
        [hashtable] $Body,
        [string] $File,
        [switch] $AllowMissing,
        [switch] $NoContent
    )
    $request = @{
        Uri = $Uri; Method = $Method; Headers = $headers; TimeoutSec = 45
        HttpVersion = '1.1'; MaximumRedirection = 0; SkipHttpErrorCheck = $true
    }
    if ($Body) {
        $request.ContentType = 'application/json'
        $request.Body = ConvertTo-Json -InputObject $Body -Compress
    }
    if ($File) {
        $request.ContentType = 'application/octet-stream'
        $request.InFile = $File
    }
    try {
        $response = Invoke-WebRequest @request
    }
    catch [System.Net.Http.HttpRequestException], [System.OperationCanceledException], [System.IO.IOException] {
        Stop-Transient "$Operation falhou na conexão ($($_.Exception.GetType().Name))."
    }

    $status = [int] $response.StatusCode
    if ($status -eq 404 -and $AllowMissing) { return }
    if ($status -eq 429 -or $status -ge 500) {
        Stop-Transient "$Operation recebeu HTTP $status."
    }
    if ($status -lt 200 -or $status -ge 300) {
        $detail = $null
        try { $detail = ($response.Content | ConvertFrom-Json -ErrorAction Stop).message } catch {}
        throw "$Operation recebeu HTTP $status. $detail"
    }
    if ($NoContent) { return }

    try {
        if ([string]::IsNullOrWhiteSpace($response.Content)) { throw 'Resposta vazia.' }
        ConvertFrom-Json -InputObject $response.Content -NoEnumerate
    }
    catch {
        Stop-Transient "$Operation recebeu uma resposta JSON incompleta ou inválida (HTTP $status)."
    }
}

function Get-GitHubPages([string] $Operation, [string] $Uri) {
    for ($page = 1; ; $page++) {
        $items = Invoke-GitHub $Operation GET "$($Uri)?per_page=100&page=$page"
        if ($items -isnot [array]) { Stop-Transient "$Operation recebeu uma lista inválida." }
        $items
        if ($items.Count -lt 100) { return }
    }
}

function Test-ReleaseAsset($Asset, $File) {
    return $Asset.state -eq 'uploaded' -and $Asset.size -eq $File.Size -and
        (!$Asset.digest -or $Asset.digest -ieq $File.Digest)
}

for ($attempt = 1; $attempt -le 4; $attempt++) {
    try {
        $releases = @(Get-GitHubPages 'Listar releases' "$api/releases")
        $published = $releases | Where-Object {
            !$_.draft -and $_.tag_name -match '^v(\d+\.\d+\.\d+\.\d+)$' -and [version] $Matches[1] -ge $version
        } | Select-Object -First 1
        if ($published) {
            Write-Output "A versão $($published.tag_name) já está publicada. Nenhuma release criada."
            return
        }

        $remoteTag = Invoke-GitHub 'Consultar tag' GET "$api/git/ref/tags/$tag" -AllowMissing
        if ($remoteTag -and ($remoteTag.object.type -ne 'commit' -or $remoteTag.object.sha -ne $Commit)) {
            throw "A tag $tag já existe e aponta para outro commit."
        }

        $draft = $releases | Where-Object { $_.draft -and $_.tag_name -eq $tag } | Select-Object -First 1
        if (!$draft) {
            Write-Output "Criando rascunho $tag."
            $draft = Invoke-GitHub 'Criar rascunho' POST "$api/releases" -Body @{
                tag_name = $tag; target_commitish = $Commit; draft = $true
                name = "Frame $version"; body = "Jellyfin.Plugin.Frame $version"
            }
        }
        elseif (!$remoteTag -and $draft.target_commitish -ne $Commit) {
            $previous = $draft.target_commitish
            if ($previous -notmatch '^[0-9a-f]{40}$') { throw "O commit do rascunho $tag é inválido." }
            git -C $root merge-base --is-ancestor $previous $Commit
            if ($LASTEXITCODE -ne 0) { throw "O commit do rascunho $tag não está no histórico da main." }
            Write-Output "Retomando o rascunho $tag criado em um commit anterior da main."
        }
        if (!$draft.draft -or $draft.tag_name -ne $tag -or $draft.id -notmatch '^[1-9]\d*$') {
            Stop-Transient "O GitHub não retornou um rascunho válido para $tag."
        }

        $draftApi = "$api/releases/$($draft.id)"
        $draftAssets = @(Get-GitHubPages 'Listar arquivos do rascunho' "$draftApi/assets")
        foreach ($file in $files) {
            $asset = $draftAssets | Where-Object name -CEQ $file.Name | Select-Object -First 1
            if ($asset -and (Test-ReleaseAsset $asset $file)) { continue }
            if ($asset) {
                Invoke-GitHub "Remover upload parcial $($file.Name)" DELETE "$api/releases/assets/$($asset.id)" -NoContent -AllowMissing
            }
            Write-Output "Enviando $($file.Name)."
            $name = [Uri]::EscapeDataString($file.Name)
            $uploaded = Invoke-GitHub "Enviar $($file.Name)" POST "$uploads/releases/$($draft.id)/assets?name=$name" -File $file.Path
            if (!(Test-ReleaseAsset $uploaded $file)) {
                Stop-Transient "O GitHub não confirmou o upload de $($file.Name)."
            }
        }

        Write-Output "Publicando $tag."
        $published = Invoke-GitHub 'Publicar rascunho' PATCH $draftApi -Body @{
            draft = $false; target_commitish = $Commit; make_latest = 'true'
        }
        if ($published.draft -ne $false -or $published.tag_name -ne $tag) {
            Stop-Transient "O GitHub não confirmou a publicação de $tag."
        }
        Write-Output "Release $tag publicada."
        return
    }
    catch {
        if (!$_.Exception.Data['GitHubTransient'] -or $attempt -eq 4) { throw }
        Write-Warning "$($_.Exception.Message) Retomando a partir do estado no GitHub ($attempt de 4)."
        Start-Sleep -Seconds ([int] [math]::Pow(2, $attempt))
    }
}
