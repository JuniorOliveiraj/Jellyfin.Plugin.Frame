param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string] $Repository
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/Jellyfin.Plugin.Frame/Jellyfin.Plugin.Frame.csproj'
[xml] $settings = Get-Content -LiteralPath $project -Raw
$frameworks = $settings.SelectSingleNode('/Project/PropertyGroup/TargetFrameworks').InnerText.Split(';', [StringSplitOptions]::TrimEntries)
$builds = @(foreach ($framework in $frameworks) {
    $evaluated = dotnet msbuild $project -nologo -p:Configuration=Release "-p:TargetFramework=$framework" '-getProperty:Version,JellyfinVersion,TargetPath'
    if ($LASTEXITCODE -ne 0) { throw "Não foi possível avaliar o projeto para $framework." }
    $build = ($evaluated | Out-String | ConvertFrom-Json).Properties
    [pscustomobject]@{
        Version = [version] $build.Version
        Minimum = [version] $build.JellyfinVersion
        Dll = $build.TargetPath
    }
})
$builds = @($builds | Sort-Object Version -Descending)
if ($builds.Count -ne 2 -or $builds[0].Version -le $builds[1].Version -or
    $builds[0].Minimum -le $builds[1].Minimum) {
    throw 'O pacote do Jellyfin mais recente deve ter a maior versão do plugin.'
}
$tag = "v$($builds[0].Version)"
if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -ne $tag) {
    throw "A tag deve corresponder à versão do projeto ($tag)."
}

dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'O build falhou.' }
$release = Join-Path $root 'artifacts/release'
New-Item -ItemType Directory -Path $release -Force | Out-Null
$timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
$versions = @(foreach ($build in $builds) {
    $minimum = $build.Minimum
    $abi = [version]::new($minimum.Major, $minimum.Minor, $minimum.Build, 0)
    $name = "Jellyfin.Plugin.Frame-$($build.Version).zip"
    $zip = Join-Path $release $name
    Compress-Archive -LiteralPath $build.Dll -DestinationPath $zip -Force
    $sha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$sha256  $name" | Set-Content -LiteralPath (Join-Path $release "$name.sha256") -Encoding ascii
    [ordered]@{
        version = $build.Version.ToString()
        changelog = "Frame $($build.Version) para Jellyfin $($build.Minimum)"
        targetAbi = $abi.ToString()
        sourceUrl = "https://github.com/$Repository/releases/download/$tag/$name"
        checksum = (Get-FileHash -LiteralPath $zip -Algorithm MD5).Hash.ToLowerInvariant()
        timestamp = $timestamp
    }
})
$manifest = @(
    [ordered]@{
        guid = 'bb053bff-fcf8-4e86-b3b6-83b83866063e'
        name = 'Frame'
        description = 'Sincroniza o catálogo Frame em bibliotecas Jellyfin.'
        overview = 'Filmes e séries via STRM e NFO.'
        owner = $Repository.Split('/')[0]
        imageUrl = "https://raw.githubusercontent.com/$Repository/refs/tags/$tag/src/Jellyfin.Plugin.Frame/Frame-icon.png"
        category = 'General'
        versions = $versions
    }
)
ConvertTo-Json -InputObject $manifest -Depth 6 | Set-Content -LiteralPath (Join-Path $release 'manifest.json') -Encoding utf8
