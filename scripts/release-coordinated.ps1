# Release one or both products independently.
#
# Usage:
#   .\scripts\release-coordinated.ps1 -Tui2Version 0.2.2                    # TUI2 only
#   .\scripts\release-coordinated.ps1 -GuiVersion 0.4.0                     # GUI only
#   .\scripts\release-coordinated.ps1 -Tui2Version 0.2.2 -GuiVersion 0.4.0  # both
#   Add -DryRun to preview without committing.
param(
    [Parameter(Mandatory=$false)][string]$Tui2Version,
    [Parameter(Mandatory=$false)][string]$GuiVersion,
    [switch]$DryRun,
    [switch]$Sign
)

if (-not $Tui2Version -and -not $GuiVersion) {
    Write-Error "Specify at least one of -Tui2Version or -GuiVersion."
    Write-Host "Usage: .\scripts\release-coordinated.ps1 [-Tui2Version X.Y.Z] [-GuiVersion X.Y.Z] [-DryRun] [-Sign]"
    exit 1
}

function Bump-Version($file, $version) {
    if (-not (Test-Path $file)) { Write-Error "File not found: $file"; exit 2 }
    if ($DryRun) {
        Write-Host "[dry-run] Would update $file -> $version"
    } else {
        $json = Get-Content $file -Raw | ConvertFrom-Json
        $json.version = $version
        ($json | ConvertTo-Json -Depth 10) + "`n" | Set-Content $file -Encoding UTF8 -NoNewline
        Write-Host "Updated $file"
    }
}

$changedFiles   = @()
$tags           = @()
$commitMsgParts = @()

if ($Tui2Version) {
    Bump-Version 'src/Leviathan.TUI2/version.json' $Tui2Version
    $changedFiles   += 'src/Leviathan.TUI2/version.json'
    $tags           += "tui2/v$Tui2Version"
    $commitMsgParts += "tui2 $Tui2Version"
}

if ($GuiVersion) {
    Bump-Version 'src/Leviathan.GUI/version.json' $GuiVersion
    $changedFiles   += 'src/Leviathan.GUI/version.json'
    $tags           += "gui/v$GuiVersion"
    $commitMsgParts += "gui $GuiVersion"
}

if ($DryRun) {
    Write-Host "[dry-run] Would commit: $($commitMsgParts -join ', ')"
    foreach ($tag in $tags) { Write-Host "[dry-run] Would create tag $tag" }
    Write-Host "Dry run complete"
    exit 0
}

$commitMsg = "Bump version: $($commitMsgParts -join ', ')"
git add @($changedFiles)
git commit -m $commitMsg
if ($LASTEXITCODE -ne 0) { Write-Error "Git commit failed"; exit 3 }

foreach ($tag in $tags) {
    if ($Sign) {
        git tag -s $tag -m "Release $tag"
    } else {
        git tag -a $tag -m "Release $tag"
    }
    Write-Host "Created tag $tag"
}

git push origin --follow-tags
Write-Host "Pushed tags: $($tags -join ', ')"
