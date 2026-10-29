using Aspire.Hosting.Azure;
using Azure.Provisioning.Roles;

var builder = DistributedApplication.CreateBuilder(args);

// The existing ACA Sandbox (runs the Python the AI writes) and the Foundry model (another tenant).
// Locally the values come from this project's user-secrets (Parameters:*); azd asks for them once
// and keeps them in .azure/<environment>/.
// The descriptions show up when 'aspire deploy' asks for the values.
var sandboxManagementUrl = builder.AddParameter("sandbox-management-url")
    .WithDescription("Sandboxes data plane, e.g. https://management.swedencentral.azuredevcompute.io (not the sandboxes.azure.com portal)");
var sandboxSubscriptionId = builder.AddParameter("sandbox-subscription-id")
    .WithDescription("Subscription ID of the sandbox group");
var sandboxResourceGroup = builder.AddParameter("sandbox-resource-group")
    .WithDescription("Resource group of the sandbox group");
var sandboxGroup = builder.AddParameter("sandbox-group")
    .WithDescription("Sandbox group name");
var sandboxId = builder.AddParameter("sandbox-id")
    .WithDescription("Sandbox ID (from 'aca sandbox list')");
var foundryEndpoint = builder.AddParameter("foundry-endpoint")
    .WithDescription("OpenAI v1 base URL ending in /openai/v1, e.g. https://<resource>.services.ai.azure.com/openai/v1");
var foundryDeployment = builder.AddParameter("foundry-deployment")
    .WithDescription("Model deployment name");
var foundryKey = builder.AddParameter("foundry-key", secret: true)
    .WithDescription("API key of the Foundry resource (other tenant)");

var web = builder.AddProject<Projects.PumpAlarms_Web>("pump-alarms")
    .WithExternalHttpEndpoints()
    .WithEnvironment("Sandbox__ManagementUrl", sandboxManagementUrl)
    .WithEnvironment("Sandbox__SubscriptionId", sandboxSubscriptionId)
    .WithEnvironment("Sandbox__ResourceGroup", sandboxResourceGroup)
    .WithEnvironment("Sandbox__SandboxGroup", sandboxGroup)
    .WithEnvironment("Sandbox__SandboxId", sandboxId)
    .WithEnvironment("Foundry__Endpoint", foundryEndpoint)
    .WithEnvironment("Foundry__Deployment", foundryDeployment)
    .WithEnvironment("Foundry__ApiKey", foundryKey);

if (builder.ExecutionContext.IsPublishMode)
{
    // Azure Container Apps environment (Consumption) with the Aspire dashboard; Aspire adds the registry.
    builder.AddAzureContainerAppEnvironment("cae").WithDashboard();

    // The app's own managed identity. Its fixed name lets the azd hook (scripts/grant-sandbox-role.ps1)
    // give it "Container Apps SandboxGroup Data Owner" on the existing sandbox group.
    var identity = builder.AddAzureUserAssignedIdentity("pump-identity")
        .ConfigureInfrastructure(infra =>
            infra.GetProvisionableResources().OfType<UserAssignedIdentity>().Single().Name = "pump-alarms-identity");

    web.WithAzureUserAssignedIdentity(identity);
}

builder.Build().Run();
