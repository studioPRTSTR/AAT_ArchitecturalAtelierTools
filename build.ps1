param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Feed = "",

    [string]$Project = ""
)

$ErrorActionPreference = "Stop"

if (-not $Project) {
    $found = @(Get-ChildItem $PSScriptRoot -Filter *.rhproj)
    if ($found.Count -ne 1) {
        throw "Expected exactly one .rhproj in '$PSScriptRoot', found $($found.Count). Pass -Project explicitly."
    }
    $Project = $found[0].FullName
}

$rhinoCode = "C:\Program Files\Rhino 8\System\RhinoCode.exe"

if ($Version -notmatch '^\d+\.\d+(\.\d+)?(-[0-9A-Za-z.]+)?$') {
    throw "Version '$Version' must look like 1.0, 1.0.3 or 1.1.0-beta."
}
if (-not (Test-Path $Project)) {
    throw "Project '$Project' not found. Create it once in Rhino's ScriptEditor (File > Create Project) and save it as ArchitecturalAtelierTools.rhproj in this folder."
}

$versionPattern = "-" + [regex]::Escape($Version) + "(\+\d+)?-"
if ($Feed -and (Get-ChildItem $Feed -Filter *.yak -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $versionPattern })) {
    throw "Version $Version already exists in '$Feed'. Bump the version number."
}

$buildPath = Join-Path $PSScriptRoot "build\$Version"
if (Test-Path $buildPath) {
    Remove-Item $buildPath -Recurse -Force
}

Write-Host "Building ArchitecturalAtelierTools $Version ..."
& $rhinoCode project build $Project --buildversion $Version --buildtarget "8.*" --buildpath $buildPath
if ($LASTEXITCODE -ne 0) {
    throw "rhinocode build failed (exit code $LASTEXITCODE). Is Rhino 8 running?"
}

$yak = Get-ChildItem $buildPath -Recurse -Filter *.yak | Select-Object -First 1
if (-not $yak) {
    throw "Build finished but no .yak package was found in '$buildPath'."
}
Write-Host "Package: $($yak.FullName)"

if ($Feed) {
    New-Item -ItemType Directory -Force -Path $Feed | Out-Null
    Copy-Item $yak.FullName $Feed
    Write-Host "Published to feed: $Feed"
}
