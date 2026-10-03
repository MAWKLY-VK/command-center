# Builds the release: one CommandCenter.exe and the update.json that goes with it. The program runs on the
# .NET 10 Desktop Runtime (x86) that the Generals Online launcher already installs, so it is a few megabytes.
# Both land in publish/. Attach them to a GitHub release tagged v<version>; older copies of
# Command Center then ask to update.
#
#   powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Notes "What changed"

param(
    [string]$Notes = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "CommandCenter\CommandCenter.csproj"
$build = Join-Path $root "CommandCenter\bin\publish"
$out = Join-Path $root "publish"

# The version comes from <Version> in the project file
[xml]$xml = Get-Content $project
$version = @($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { throw "No <Version> found in $project" }
Write-Host "Command Center $version"

dotnet publish $project -c Release -r win-x86 --self-contained false -o $build -p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

New-Item -ItemType Directory -Force $out | Out-Null
$exe = Join-Path $out "CommandCenter.exe"
Copy-Item (Join-Path $build "CommandCenter.exe") $exe -Force
$sha = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()

$manifest = [ordered]@{
    version = $version
    url     = "https://github.com/MAWKLY-VK/command-center/releases/download/v$version/CommandCenter.exe"
    sha256  = $sha
    notes   = $Notes
}
$json = $manifest | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $out "update.json"), $json, (New-Object System.Text.UTF8Encoding $false))

$size = (Get-Item $exe).Length / 1MB
Write-Host ("publish\CommandCenter.exe  {0:0.0} MB" -f $size)
Write-Host "publish\update.json        sha256 $sha"
