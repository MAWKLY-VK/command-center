<#
.SYNOPSIS
Takes --capture screenshots of every page and tab of one Command Center build, so that two builds can be compared with
tools/Compare-Captures.cs.

.DESCRIPTION
Each run uses a fresh temporary data folder (always at the same path, emptied before and deleted after), the drawn backdrop (so Steam's art is never read) and
the repository's go-profile.json (so the GO profile does not come from the network). The game folder must be an
isolated copy of the test game: the real Steam installation is refused, and the copy is fingerprinted before and after
so that any change to it is reported.

Output: <Out>\pages\NN-<page>.png for every page and tab, and <Out>\samples\NN-<page>.png with the made-up health
problems (--samples), plus <Out>\capture.json describing the run and <Out>\ignore.txt (a copy of
tools/capture-ignore.txt, the areas that change between runs of the same build).

.EXAMPLE
tools\Capture-Pages.ps1 -Exe ..\cc-reference\0.4.0\CommandCenter.exe -Game "..\cc-reference\game\Command & Conquer Generals - Zero Hour" -Out ..\cc-reference\captures\before
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Game,
    [Parameter(Mandatory)] [string] $Out,
    [string] $Size = '1600x1000',
    [string] $GoProfile = (Join-Path $PSScriptRoot '..\go-profile.json'),
    [int] $TimeoutSeconds = 600
)
$ErrorActionPreference = 'Stop'

# Every page and tab. options:network:run is left out because it runs live network diagnostics.
$pages = @(
    'home',
    'tools:health', 'tools:health:history', 'tools:health:questions',
    'tools:maps:installed', 'tools:maps:library',
    'tools:replays',
    'tools:hotkeys:buttons', 'tools:hotkeys:keys', 'tools:hotkeys:letters',
    'tools:addons',
    'options:language', 'options:game', 'options:camera', 'options:chat', 'options:input', 'options:graphics',
    'options:social', 'options:network', 'options:datapacks', 'options:plugins', 'options:launcher'
)
# The pages that show the made-up health problems
$samplePages = @('home', 'tools:health')

$Exe = (Resolve-Path -LiteralPath $Exe).Path
$Game = (Resolve-Path -LiteralPath $Game).Path
$GoProfile = (Resolve-Path -LiteralPath $GoProfile).Path
$Out = [IO.Path]::GetFullPath($Out)

if ($Game -match '\\steamapps\\') {
    throw "Refusing the Steam installation ($Game). Copy the test game to its own folder and pass the copy."
}
foreach ($file in 'WindowZH.big', 'INIZH.big') {
    if (-not (Test-Path -LiteralPath (Join-Path $Game $file))) { throw "$Game is not a Zero Hour folder: $file is missing." }
}
if ((Test-Path -LiteralPath $Out) -and @(Get-ChildItem -LiteralPath $Out -Force).Count -gt 0) {
    throw "$Out already has files. Pass a new folder, so that captures from different runs never mix."
}

function Get-Fingerprint([string] $folder) {
    Get-ChildItem -LiteralPath $folder -Recurse -File -Force |
        Sort-Object FullName |
        ForEach-Object { '{0}|{1}|{2}' -f $_.FullName.Substring($folder.Length), $_.Length, $_.LastWriteTimeUtc.Ticks }
}

function Invoke-Capture([string] $folder, [string[]] $pageList, [switch] $Samples) {
    # Always the same path, emptied first: pages show the data folder's path, so a random name would differ per run
    $data = Join-Path ([IO.Path]::GetTempPath()) 'cc-capture-data'
    if (Test-Path -LiteralPath $data) { [IO.Directory]::Delete($data, $true) }
    [void][IO.Directory]::CreateDirectory($data)
    try {
        $info = [System.Diagnostics.ProcessStartInfo]::new($Exe)
        $info.UseShellExecute = $false
        $arguments = @('--game', $Game, '--user-data', $data, '--capture', $folder, '--size', $Size,
            '--pages', ($pageList -join ','), '--drawn-backdrop', '--go-profile', $GoProfile)
        if ($Samples) { $arguments += '--samples' }
        foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
        $process = [System.Diagnostics.Process]::Start($info)
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill()
            throw "The capture run did not finish within $TimeoutSeconds seconds."
        }
        $taken = @(Get-ChildItem -LiteralPath $folder -Filter *.png -ErrorAction SilentlyContinue).Count
        if ($taken -ne $pageList.Count) { throw "Expected $($pageList.Count) captures in $folder, found $taken." }
        "{0}: {1} captures" -f (Split-Path $folder -Leaf), $taken
    }
    finally {
        if (Test-Path -LiteralPath $data) { [IO.Directory]::Delete($data, $true) }
    }
}

$before = Get-Fingerprint $Game
Invoke-Capture (Join-Path $Out 'pages') $pages
Invoke-Capture (Join-Path $Out 'samples') $samplePages -Samples
$after = Get-Fingerprint $Game

$changed = @(Compare-Object $before $after | ForEach-Object { $_.InputObject.Split('|')[0] } | Sort-Object -Unique)
# The areas that change between runs, for tools/Compare-Captures.cs when this set is the "before" side
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'capture-ignore.txt') -Destination (Join-Path $Out 'ignore.txt')

[ordered]@{
    exe     = $Exe
    version = (Get-Item -LiteralPath $Exe).VersionInfo.ProductVersion
    game    = $Game
    size    = $Size
    taken   = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Out 'capture.json') -Encoding utf8

if ($changed.Count -gt 0) {
    Write-Warning "The game copy changed during the captures: $($changed -join ', ')"
    exit 1
}
'The game copy is unchanged.'
