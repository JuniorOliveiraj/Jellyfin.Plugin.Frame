[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $LibraryPath,

    [Parameter(Mandatory)]
    [uri] $PlaybackUrl,

    [ValidateNotNullOrEmpty()]
    [string] $Title = 'Frame Teste'
)

$ErrorActionPreference = 'Stop'
if (!$PlaybackUrl.IsAbsoluteUri -or $PlaybackUrl.Scheme -ne 'https' -or $PlaybackUrl.UserInfo -or $PlaybackUrl.OriginalString -match '[\r\n]') {
    throw 'Informe uma URL HTTPS direta, sem usuario e senha.'
}

$movieId = '5207a7c3-510c-46e2-804d-8efab1083a51'
$movieDirectory = Join-Path $LibraryPath "Frame Teste [$movieId]"
[IO.Directory]::CreateDirectory($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($movieDirectory)) | Out-Null

$nfo = New-Object System.Xml.XmlDocument
$movie = $nfo.AppendChild($nfo.CreateElement('movie'))
$fields = [ordered]@{
    title = $Title
    plot = 'Filme de teste para validar a importacao de midia do Frame.'
    uniqueid = $movieId
}
foreach ($field in $fields.GetEnumerator()) {
    $element = $movie.AppendChild($nfo.CreateElement($field.Key))
    $element.InnerText = $field.Value
    if ($field.Key -eq 'uniqueid') { $element.SetAttribute('type', 'frame') }
}

$files = @{
    'movie.nfo' = $nfo.OuterXml + "`n"
    'Frame Teste.strm' = $PlaybackUrl.AbsoluteUri + "`n"
}
foreach ($file in $files.GetEnumerator()) {
    $path = Join-Path $movieDirectory $file.Key
    if (!(Test-Path -LiteralPath $path) -or (Get-Content -LiteralPath $path -Raw) -cne $file.Value) {
        [IO.File]::WriteAllText($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path), $file.Value)
    }
}
Get-Item -LiteralPath $movieDirectory
