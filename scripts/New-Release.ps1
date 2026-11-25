param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string] $Repository
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/Jellyfin.Plugin.Frame/Jellyfin.Plugin.Frame.csproj'
[xml] $settings = Get-Content -LiteralPath $project -Raw
$version = [string] $settings.Project.PropertyGroup.Version
$minimum = [version] (($settings.Project.ItemGroup.PackageReference |
    Where-Object Include -eq 'Jellyfin.Controller').Version)
$abi = [version]::new($minimum.Major, $minimum.Minor, $minimum.Build, 0)
$tag = "v$version"
if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -ne $tag) {
    throw "A tag deve corresponder à versão do projeto ($tag)."
}

dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'O build falhou.' }
$framework = [string] $settings.Project.PropertyGroup.TargetFramework
$dll = Join-Path (Split-Path -Parent $project) "bin/Release/$framework/Jellyfin.Plugin.Frame.dll"
$release = Join-Path $root 'artifacts/release'
New-Item -ItemType Directory -Path $release -Force | Out-Null
$name = "Jellyfin.Plugin.Frame-$version.zip"
$zip = Join-Path $release $name
Compress-Archive -LiteralPath $dll -DestinationPath $zip -Force
$manifest = @(
    [ordered]@{
        guid = 'bb053bff-fcf8-4e86-b3b6-83b83866063e'
        name = 'Frame'
        description = 'Sincroniza o catálogo Frame em bibliotecas Jellyfin.'
        overview = 'Filmes e séries via STRM e NFO.'
        owner = $Repository.Split('/')[0]
        category = 'General'
        versions = @(
            [ordered]@{
                version = $version
                changelog = "Frame $version"
                targetAbi = $abi.ToString()
                sourceUrl = "https://github.com/$Repository/releases/download/$tag/$name"
                checksum = (Get-FileHash -LiteralPath $zip -Algorithm MD5).Hash.ToLowerInvariant()
                timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
            }
        )
    }
)
ConvertTo-Json -InputObject $manifest -Depth 6 | Set-Content -LiteralPath (Join-Path $release 'manifest.json') -Encoding utf8
$sha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$sha256  $name" | Set-Content -LiteralPath (Join-Path $release "$name.sha256") -Encoding ascii
