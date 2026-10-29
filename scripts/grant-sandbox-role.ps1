# Gives the deployed app's managed identity access to the existing ACA Sandbox group.
# Run once after the first 'aspire deploy' (re-running is safe):  .\scripts\grant-sandbox-role.ps1
#
# Aspire creates the identity 'pump-alarms-identity' (see AppHost.cs). The sandbox group is not
# managed by Aspire, so this role assignment is done here, by role name.
param(
  [string]$IdentityName = "pump-alarms-identity",
  [string]$Role         = "Container Apps SandboxGroup Data Owner"
)
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

$settings = Get-Content "$PSScriptRoot\..\src\PumpAlarms.Web\appsettings.Development.json" -Raw | ConvertFrom-Json
$sandbox  = $settings.Sandbox

Write-Host "1/4 Select subscription $($sandbox.SubscriptionId)"
az account set --subscription $sandbox.SubscriptionId

Write-Host "2/4 Find identity $IdentityName"
$principalId = az identity list --query "[?name=='$IdentityName'].principalId | [0]" -o tsv
if (-not $principalId) { throw "Identity '$IdentityName' not found. Run 'aspire deploy' first." }
Write-Host "    principal $principalId"

Write-Host "3/4 Find sandbox group $($sandbox.SandboxGroup)"
$scope = az resource list -g $sandbox.ResourceGroup --name $sandbox.SandboxGroup --query "[0].id" -o tsv
if (-not $scope) { throw "Sandbox group '$($sandbox.SandboxGroup)' not found in $($sandbox.ResourceGroup)." }
Write-Host "    $scope"

Write-Host "4/4 Assign '$Role'"
$existing = az role assignment list --assignee $principalId --scope $scope --role $Role --query "[].id" -o tsv
if ($existing) {
  Write-Host "Already assigned: $Role -> $IdentityName on $($sandbox.SandboxGroup)"
} else {
  az role assignment create --assignee-object-id $principalId --assignee-principal-type ServicePrincipal `
    --role $Role --scope $scope -o none
  Write-Host "Assigned: $Role -> $IdentityName on $($sandbox.SandboxGroup)" -ForegroundColor Green
  Write-Host "It can take a few minutes before the app's sandbox calls stop returning 403."
}
