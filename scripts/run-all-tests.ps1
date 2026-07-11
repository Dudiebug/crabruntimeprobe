[CmdletBinding()]
param(
  [switch]$SkipLegacy,
  [switch]$SkipDashboard
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$PowerShellExe = (Get-Command powershell.exe -ErrorAction Stop).Source
$failures = New-Object System.Collections.Generic.List[string]
$started = Get-Date

if (-not $SkipLegacy) {
  $tests = Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter "test-*.ps1" |
    Sort-Object Name
  foreach ($test in $tests) {
    Write-Host "`n== $($test.Name) ==" -ForegroundColor Cyan
    & $PowerShellExe -NoProfile -ExecutionPolicy Bypass -File $test.FullName
    if ($LASTEXITCODE -ne 0) {
      $failures.Add("$($test.Name) exited $LASTEXITCODE") | Out-Null
    }
  }
}

if (-not $SkipDashboard) {
  $dashboardTests = Join-Path $RepoRoot "dashboard\tests\CrabRuntimeProbe.Dashboard.Tests\CrabRuntimeProbe.Dashboard.Tests.csproj"
  if (-not (Test-Path -LiteralPath $dashboardTests -PathType Leaf)) {
    $failures.Add("Dashboard test project is missing: $dashboardTests") | Out-Null
  } else {
    Write-Host "`n== Dashboard core tests ==" -ForegroundColor Cyan
    & dotnet run --project $dashboardTests -c Release
    if ($LASTEXITCODE -ne 0) {
      $failures.Add("Dashboard tests exited $LASTEXITCODE") | Out-Null
    }
  }
}

$elapsed = (Get-Date) - $started
if ($failures.Count -gt 0) {
  Write-Host "`nTest run failed after $([math]::Round($elapsed.TotalSeconds, 1))s:" -ForegroundColor Red
  $failures | ForEach-Object { Write-Host " - $_" -ForegroundColor Red }
  exit 1
}

Write-Host "`nAll tests passed in $([math]::Round($elapsed.TotalSeconds, 1))s." -ForegroundColor Green
exit 0
