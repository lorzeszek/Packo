param(
    [ValidatePattern('^[a-z]{20}$')]
    [string]$ProjectRef = 'dahwafdelpczzbgvlelc'
)
$ErrorActionPreference = 'Stop'
$placesRepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $placesRepoRoot
try {
    # These are authenticated, project-scoped operations. No secret values are read.
    & supabase secrets list "--remote=$ProjectRef" --agent=no
    if ($LASTEXITCODE -ne 0) { throw 'Run supabase login in this terminal, then retry this script.' }
    & supabase db query "--remote=$ProjectRef" --file (Join-Path $placesRepoRoot 'Database/Migrations/20261122_add_places_quota.sql') --yes --agent=no
    if ($LASTEXITCODE -ne 0) { throw 'Places quota migration failed. The function was not deployed.' }
    & supabase db query "--remote=$ProjectRef" --file (Join-Path $placesRepoRoot 'Database/Migrations/20261122_describe_places_limits.sql') --yes --agent=no
    if ($LASTEXITCODE -ne 0) { throw 'Places limit diagnostics migration failed. The function was not deployed.' }
    & supabase functions deploy trip-places "--remote=$ProjectRef" --use-api --yes --agent=no
    if ($LASTEXITCODE -ne 0) { throw 'Places Edge Function deployment failed.' }
    Write-Output 'Places quota and trip-places deployed. Test a search from a signed-in app session.'
}
finally { Pop-Location }
