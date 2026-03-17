param(
    [Parameter(Mandatory=$false)][string]$Version,
    [switch]$DryRun,
    [switch]$Sign
)

if (-not $Version) {
    Write-Host "Usage: .\scripts\release-coordinated.ps1 -Version <X.Y.Z> [-DryRun] [-Sign]"
    exit 1
}

$files = @(
    'src/Leviathan.GUI/version.json',
    'src/Leviathan.TUI2/version.json'
)

Write-Host "Bumping versions to $Version"
foreach ($f in $files) {
    if (-not (Test-Path $f)) { Write-Error "File not found: $f"; exit 2 }
    $json = Get-Content $f -Raw | ConvertFrom-Json
    $json.version = $Version
    $out = $json | ConvertTo-Json -Depth 10
    if ($DryRun) {
        Write-Host "[dry-run] Would update $f -> $Version"
    } else {
        $out | Set-Content $f -Encoding UTF8
        Write-Host "Updated $f"
    }
}

if ($DryRun) { Write-Host "Dry run complete"; exit 0 }

git add @($files)
git commit -m "Bump versions to $Version"
if ($LASTEXITCODE -ne 0) { Write-Error "Git commit failed"; exit 3 }

$tag = "v$Version"
if ($Sign) {
    git tag -s $tag -m "Release $tag"
} else {
    git tag -a $tag -m "Release $tag"
}
git push origin --follow-tags
Write-Host "Created tag $tag and pushed changes"
