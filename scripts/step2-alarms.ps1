# Step 2: copy the pump alarm file and a fixed pandas script into the sandbox, then run it. No AI yet.
# Run from anywhere:  .\scripts\step2-alarms.ps1 <sandbox-id>
# (the ID comes from 'aca sandbox list'; -SandboxId <id> works too)
param(
  [Parameter(Mandatory, Position = 0)][string]$SandboxId,
  [string]$CsvPath = "$PSScriptRoot\..\samples\pump_alarms.csv"
)
$ErrorActionPreference = "Stop"

# aca resolves --file against the current directory, so pass full paths.
$csv    = (Resolve-Path $CsvPath).Path
$script = (Resolve-Path "$PSScriptRoot\sandbox\analyze_fixed.py").Path

# aca does not always return a failing exit code, so stop on any error text too.
function Invoke-Aca {
  $out = & aca @args 2>&1 | Out-String
  Write-Host $out.TrimEnd()
  if ($LASTEXITCODE -ne 0 -or $out -match '^Error:') { throw "aca $($args -join ' ') failed" }
}

Write-Host "== Copy files into sandbox $SandboxId" -ForegroundColor Cyan
Write-Host "   $csv"
Invoke-Aca sandbox fs write --path /root/pump_alarms.csv --file $csv --id $SandboxId
Write-Host "   $script"
Invoke-Aca sandbox fs write --path /root/analyze_fixed.py --file $script --id $SandboxId

Write-Host "== Run the analysis in the sandbox" -ForegroundColor Cyan
Invoke-Aca sandbox exec -c "python3 /root/analyze_fixed.py" --id $SandboxId
