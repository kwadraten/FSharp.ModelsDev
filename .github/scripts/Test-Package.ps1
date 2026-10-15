[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [string] $PackageDirectory = 'artifacts/packages'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$packageRoot = if ([System.IO.Path]::IsPathRooted($PackageDirectory)) {
    [System.IO.Path]::GetFullPath($PackageDirectory)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $PackageDirectory))
}
$package = Join-Path $packageRoot "FSharp.ModelsDev.$Version.nupkg"
$symbols = Join-Path $packageRoot "FSharp.ModelsDev.$Version.snupkg"
foreach ($path in @($package, $symbols)) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Package artifact is missing: $path" }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($package)
try {
    foreach ($name in @('lib/net8.0/FSharp.ModelsDev.dll', 'lib/net8.0/FSharp.ModelsDev.xml', 'README.md', 'README.zh-CN.md', 'LICENSE')) {
        if (!$archive.GetEntry($name)) { throw "Package entry is missing: $name" }
    }
    $entry = $archive.GetEntry('FSharp.ModelsDev.nuspec')
    if (!$entry) { throw 'Package manifest is missing.' }
    $reader = [System.IO.StreamReader]::new($entry.Open())
    try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $metadata = $manifest.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    foreach ($pair in @{ id = 'FSharp.ModelsDev'; version = $Version; readme = 'README.md' }.GetEnumerator()) {
        $node = $metadata.SelectSingleNode("*[local-name()='$($pair.Key)']")
        if (!$node -or $node.InnerText -cne $pair.Value) { throw "Unexpected package metadata: $($pair.Key)" }
    }
    $license = $metadata.SelectSingleNode('*[local-name()="license"]')
    if (!$license -or $license.InnerText -ne 'MIT' -or $license.GetAttribute('type') -ne 'expression') {
        throw 'The package must declare its MIT license.'
    }
    $repository = $metadata.SelectSingleNode('*[local-name()="repository"]')
    if (!$repository -or $repository.GetAttribute('url') -ne 'https://github.com/kwadraten/FSharp.ModelsDev') {
        throw 'Package repository metadata is missing or incorrect.'
    }
} finally { $archive.Dispose() }
$archive = [System.IO.Compression.ZipFile]::OpenRead($symbols)
try {
    if (!$archive.GetEntry('lib/net8.0/FSharp.ModelsDev.pdb')) { throw 'Symbol package PDB is missing.' }
} finally { $archive.Dispose() }

# A fresh package cache and source mapping force the consumers to install this
# artifact, not a project reference, an old cache entry, or nuget.org's copy.
$smokeRoot = Join-Path $repoRoot ('artifacts/package-smoke/' + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($smokeRoot) | Out-Null
$escapedSource = [System.Security.SecurityElement]::Escape($packageRoot)
$config = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="packed" value="$escapedSource" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="packed"><package pattern="FSharp.ModelsDev" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@
$configPath = Join-Path $smokeRoot 'NuGet.Config'
[System.IO.File]::WriteAllText($configPath, $config, [System.Text.UTF8Encoding]::new($false))
foreach ($project in @('tests/PackageSmoke.CSharp/PackageSmoke.CSharp.csproj', 'tests/PackageSmoke.FSharp/PackageSmoke.FSharp.fsproj')) {
    $projectPath = Join-Path $repoRoot $project
    dotnet restore $projectPath --configfile $configPath --packages (Join-Path $smokeRoot 'packages') "-p:ModelsDevVersion=$Version"
    if ($LASTEXITCODE -ne 0) { throw "Package consumer restore failed: $project" }
    dotnet build $projectPath -c Release --no-restore "-p:ModelsDevVersion=$Version"
    if ($LASTEXITCODE -ne 0) { throw "Package consumer build failed: $project" }
    dotnet run --project $projectPath -c Release --no-build --no-restore "-p:ModelsDevVersion=$Version"
    if ($LASTEXITCODE -ne 0) { throw "Package consumer execution failed: $project" }
}
Write-Output "Verified NuGet package $Version, symbols, metadata, and F#/C# consumers."
