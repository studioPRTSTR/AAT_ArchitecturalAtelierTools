param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Feed = "",

    [string]$Project = (Join-Path $PSScriptRoot "RSoTeSc.rhproj")
)

$ErrorActionPreference = "Stop"

$rhinoCode = "C:\Program Files\Rhino 8\System\RhinoCode.exe"

if ($Version -notmatch '^\d+\.\d+(\.\d+)?(-[0-9A-Za-z.]+)?$') {
    throw "Version '$Version' must look like 1.0, 1.0.3 or 1.1.0-beta."
}
if (-not (Test-Path $Project)) {
    throw "Project '$Project' not found. Create it once in Rhino's ScriptEditor (File > Create Project) and save it as RSoTeSc.rhproj in this folder."
}

if ($Feed -and (Get-ChildItem $Feed -Filter "*-$Version-*.yak" -ErrorAction SilentlyContinue)) {
    throw "Version $Version already exists in '$Feed'. Bump the version number."
}

$buildPath = Join-Path $PSScriptRoot "build\$Version"
if (Test-Path $buildPath) {
    Remove-Item $buildPath -Recurse -Force
}

Write-Host "Building RSoTeSc $Version ..."
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
