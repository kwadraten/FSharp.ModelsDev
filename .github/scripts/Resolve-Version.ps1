[CmdletBinding()]
param([Parameter(Mandatory)][string] $Tag)

$ErrorActionPreference = 'Stop'
$pattern = '^(?<version>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))\z'
$match = [regex]::Match($Tag, $pattern)
if (!$match.Success) {
    throw 'Use a release tag such as 0.1.0: three numeric components, without a v prefix, prerelease suffix, build metadata, or leading zeroes.'
}
$version = $match.Groups['version'].Value
if ($env:GITHUB_OUTPUT) {
    [System.IO.File]::AppendAllText($env:GITHUB_OUTPUT, "version=$version`n", [System.Text.UTF8Encoding]::new($false))
}
Write-Output $version
