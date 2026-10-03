# Builds CommandCenter\Data\game-files.json: the size and SHA-256 of Zero Hour's original archives, taken from a clean
# installation. Only fingerprints are stored, never game content. The health check compares a player's files against
# them to find damaged, missing or changed archives.
#   powershell -File tools\make-file-manifest.ps1 -Game "<Zero Hour folder>" [-Source "Zero Hour 1.04 (Steam)"]
param(
    [Parameter(Mandatory = $true)][string]$Game,
    [string]$Source = "Zero Hour 1.04 (Steam)",
    [string]$Output
)

$ErrorActionPreference = "Stop"
if (-not $Output) {
    $Output = Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) "CommandCenter\Data\game-files.json"
}

# The archives Zero Hour ships with (and the Generals data it loads from ZH_Generals). Language archives are kept
# apart: they are only checked when that language is installed.
$core = @(
    "AudioZH.big", "gensecZH.big", "INIZH.big", "MapsZH.big", "Music.big", "MusicZH.big", "PatchData.big", "PatchINI.big",
    "PatchWindow.big", "PatchZH.big", "ShadersZH.big", "SpeechZH.big", "TerrainZH.big", "TexturesZH.big", "W3DZH.big", "WindowZH.big",
    "ZH_Generals\Audio.big", "ZH_Generals\gensec.big", "ZH_Generals\INI.big", "ZH_Generals\maps.big", "ZH_Generals\Music.big",
    "ZH_Generals\Patch.big", "ZH_Generals\shaders.big", "ZH_Generals\Speech.big", "ZH_Generals\Terrain.big",
    "ZH_Generals\Textures.big", "ZH_Generals\W3D.big", "ZH_Generals\Window.big"
)
$english = @(
    "AudioEnglishZH.big", "EnglishZH.big", "SpeechEnglishZH.big", "W3DEnglishZH.big",
    "ZH_Generals\AudioEnglish.big", "ZH_Generals\English.big", "ZH_Generals\SpeechEnglish.big"
)
# Archives whose contents decide the game rules: a change there causes mismatches online
$rules = @("INIZH.big", "PatchINI.big", "PatchZH.big", "MapsZH.big", "ZH_Generals\INI.big", "ZH_Generals\maps.big", "ZH_Generals\Patch.big")

$entries = foreach ($group in @(@{ Name = "core"; Files = $core }, @{ Name = "english"; Files = $english })) {
    foreach ($file in $group.Files) {
        $path = Join-Path $Game $file
        if (-not (Test-Path -LiteralPath $path)) { throw "Missing in the reference installation: $file" }
        $item = Get-Item -LiteralPath $path
        [ordered]@{
            path   = $file
            size   = $item.Length
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            group  = $group.Name
            rules  = $rules -contains $file
        }
    }
}

$manifest = [ordered]@{ source = $Source; files = @($entries) }
New-Item -ItemType Directory -Force (Split-Path -Parent $Output) | Out-Null
[System.IO.File]::WriteAllText($Output, ($manifest | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding $false))
"Wrote $Output ($(@($entries).Count) archives)"
