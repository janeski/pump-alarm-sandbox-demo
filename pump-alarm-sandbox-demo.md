# Pump Alarm Analyzer: Azure Container Apps Sandboxes demo

A small web app where you upload a file of pump alarms and an AI model analyzes it.
The model writes Python. That Python never runs in the app. It runs in an
**Azure Container Apps Sandbox**: a hardware-isolated microVM that starts in about a second,
can be locked down to no network access, and is thrown away (or suspended) when you are done.

We build it in small steps. Each step works by itself before we start the next one.

---

## The idea in one picture

```
 Browser                 Web app (.NET, ACA)           AI model (other tenant)   ACA Sandbox
 ───────                 ───────────────────           ───────────────────────   ───────────
 upload alarms.csv ───►  1. copy file in ─────────────────────────────────────►  /root/pump_alarms.csv
                         2. "analyze this" ─────────►  writes Python
                         3. run code ◄──────────────── tool call: run_python(code)
                            ──────────────────────────────────────────────────►  python3, returns stdout
                         4. output ─────────────────►  reads result, writes summary
 summary + code ◄─────── 5. show answer
```

**Why a sandbox?** Code written by a model is untrusted code. You would not run it inside your
web app. The sandbox gives each analysis its own microVM, egress can be blocked, and the app
never shares a process with model-written code.

---

## Steps

| Step | What we build | AI? | Status |
|------|---------------|-----|--------|
| 0 | Prerequisites: `aca` CLI, sandbox group | no | done |
| 1 | A sandbox with Python and pandas | no | done (pandas 2.3.3) |
| 2 | Copy the alarm file in and run fixed pandas code | no | done |
| 3 | The .NET app calls the sandbox instead of returning stub data | no | done |
| 4 | Let the AI write the analysis code | yes | **next** |
| 5 | Deploy the app to Azure Container Apps | yes | |
| 6 | Extras: charts, follow-up questions, prepared disk image | yes | |

---

## Step 0: Prerequisites (done)

- Azure CLI signed in to the tenant that holds the sandbox group.
- The `aca` CLI (Sandboxes are managed with it, not with `az containerapp`):

```powershell
irm https://aka.ms/aca-cli-install-ps | iex
aca doctor
```

- A sandbox group (created in the portal at https://sandboxes.azure.com, or with
  `aca sandboxgroup create`). Creating it gives you the **Container Apps SandboxGroup Data Owner** role.
- .NET 10 SDK for the app.
- An AI chat model deployment. Ours is in **another tenant**, see step 4.

---

## Step 1: A sandbox with Python and pandas (done)

```powershell
aca sandbox create --disk ubuntu --label name=pump-demo
aca sandbox exec -l name=pump-demo -c "apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y python3 python3-pandas python3-matplotlib python3-openpyxl"
aca sandbox exec -l name=pump-demo -c "python3 -c 'import pandas; print(pandas.__version__)'"
```

Result: pandas 2.3.3.

What the sandbox needs: Python 3, pandas (with numpy), and later matplotlib (charts) and
openpyxl (Excel input). The install needs egress; block egress again once it is done.

---

## Step 2: Pump alarms in the sandbox (still no AI)

### The sample file: `samples/pump_alarms.csv`

| column | example | meaning |
|--------|---------|---------|
| `timestamp` | `2026-10-01T06:12:00` | when the alarm started |
| `pump_id` | `P-101` | which pump |
| `site` | `Bergen-North` | where it is |
| `alarm_code` | `HIGH_TEMP` | machine-readable type |
| `severity` | `critical` / `warning` / `info` | how bad |
| `duration_min` | `14` | how long it was active |

It has 8 rows. For the AI demo there is a month of data with patterns hidden in it,
`samples/pump_alarms_october.csv` (1512 alarms, made by `py -3.12 scripts/generate_alarms.py`):

| pump | hidden pattern |
|------|----------------|
| `P-101` | `HIGH_TEMP` at night, more often and longer each week, critical from mid-month; `VIBRATION` in the last week |
| `P-204` | `LOW_PRESSURE` every Monday morning |
| `P-402` | `DRY_RUN`, critical, from 20 October |
| `P-310` | the most alarms (564), all harmless `COMM_LOSS` info: a red herring |

### Run it

```powershell
.\scripts\step2-alarms.ps1 <sandbox-id>
```

[scripts/step2-alarms.ps1](scripts/step2-alarms.ps1) copies two files into the sandbox with
`aca sandbox fs write` and runs one with `aca sandbox exec`:

- `samples/pump_alarms.csv` → `/root/pump_alarms.csv`
- [scripts/sandbox/analyze_fixed.py](scripts/sandbox/analyze_fixed.py) → `/root/analyze_fixed.py`

**Done when** you see:

```
(8, 6)
         alarms  critical  minutes
pump_id
P-101         4         3      142
P-310         2         1      123
P-204         2         0       14
```

---

## Step 3: The .NET app talks to the sandbox (still no AI)

The UI is already built: an ASP.NET Core minimal API serving one static page.

```
src/PumpAlarms.Web/
  Program.cs                     # serves wwwroot, POST /analyze (sandbox when configured, stub otherwise)
  SandboxClient.cs               # ExecAsync(command) and WriteFileAsync(path, bytes)
  appsettings.Development.json   # sandbox IDs, git-ignored
  wwwroot/                       # index.html, app.css, app.js
samples/pump_alarms.csv
```

Run it (the launch profile sets the Development environment, so the sandbox settings load):

```powershell
dotnet run --project src/PumpAlarms.Web
```

The UI expects this contract, so the stub can be swapped for the real thing without touching the page:

```
POST /analyze   multipart/form-data: file, question
200 OK
{
  "mode": "stub" | "live",
  "sessionId": "…",            // the sandbox used
  "fileName": "pump_alarms.csv",
  "question": "…",
  "answer": "markdown: headings, lists, **bold**, `code`",
  "steps": [ { "code": "…", "stdout": "…", "stderr": "…" } ]
}
```

What `POST /analyze` does in this step: write the uploaded file to `/root/pump_alarms.csv`, write the
fixed pandas script, run it with `python3`, return the output as one step with `mode` = `"live"`.

### The Sandboxes data plane (read from `aca` debug output)

The app cannot shell out to the `aca` CLI in a container, so it makes the same REST call the CLI makes:

```
POST https://management.<region>.azuredevcompute.io/subscriptions/<sub>/resourceGroups/<rg>
     /sandboxGroups/<group>/sandboxes/<sandbox-id>/executeShellCommand?api-version=2026-02-01-preview
Authorization: Bearer <token for https://management.azuredevcompute.io/.default>

{ "command": "echo hello" }

200 OK
{ "executionTimeMs": 26, "exitCode": 0, "stderr": "", "stdout": "hello\n" }
```

- The command runs in a shell, so `&&` and `>` work.
- Sandboxes suspend after 10 idle minutes (`lifecycle.autoSuspendPolicy`); exec then returns
  `409 GlobalSandboxNotRunning`. The app first reads the sandbox (`GET …/sandboxes/<id>`, field `state`)
  and, if it is not `Running`, calls `POST …/sandboxes/<id>/resume` and waits.
  The resume path is inferred from the CLI's `resume` command, not yet seen in debug output.
- The top bar shows the sandbox state (`GET /sandbox` in the app, polled every 10 s, every 1 s while
  it changes) with a **Pause / Resume** button (`POST /sandbox/stop`, `POST /sandbox/resume`, which call
  `…/sandboxes/<id>/stop` and `…/resume`; the stop path is inferred the same way). Hover the badge for
  CPU, memory and the auto-pause interval.
- Supported api-versions (response header): `2026-02-01-preview`, `2026-09-01-preview`.
- Token scope: from the Aspire Sandboxes integration source (`AzureDevComputeClient.AuthorizationScope`).
- **Files:** the file API is not documented, so `WriteFileAsync` sends the file as base64 in 48 KB chunks
  through `executeShellCommand` and decodes it with `base64 -d`. Only the one verified call is used.

Configuration (`appsettings.Development.json` locally, environment variables in Azure):

| setting | value |
|---------|-------|
| `Sandbox:ManagementUrl` | `https://management.swedencentral.azuredevcompute.io` |
| `Sandbox:SubscriptionId` | subscription of the sandbox group |
| `Sandbox:ResourceGroup` | resource group of the sandbox group |
| `Sandbox:SandboxGroup` | sandbox group name |
| `Sandbox:SandboxId` | the sandbox with pandas (one shared sandbox for now; one per analysis later) |

Without these settings `/analyze` falls back to the stub, so the UI still works offline.

Auth: `DefaultAzureCredential` from `Azure.Identity`. Locally that is your `az login`; in Azure
the Container App's managed identity with the SandboxGroup Data Owner role.

**Done when:** you upload the CSV in the browser and see real sandbox output, badge says "Live sandbox".

---

## Step 4: Let the AI write the analysis

Replace the fixed script with a small loop. The model gets **one tool**:

```json
{
  "name": "run_python",
  "description": "Run Python in a sandbox. The alarm file is at /root/pump_alarms.csv. pandas is available. Print what you want to see.",
  "parameters": {
    "type": "object",
    "properties": { "code": { "type": "string" } },
    "required": ["code"]
  }
}
```

The loop:

1. Copy the file into the sandbox (as in step 3).
2. Read the header and first 5 rows **in the sandbox** and give them to the model, so it knows
   the columns without seeing the whole file.
3. Send the system prompt plus the user's question (default: *"Which pumps need attention and why?"*).
4. When the model calls `run_python`, write the code to `/root/step_N.py`, run `python3 /root/step_N.py`,
   and send back stdout / stderr. If the code fails, the model sees the error and fixes it.
5. Stop when the model answers in text, or after 5 tool calls.
6. Show the answer **and** every piece of code that ran. Showing the code is the point of the demo.

In .NET the loop is mostly free: `Microsoft.Extensions.AI` with `.UseFunctionInvocation()` calls
`run_python` for you until the model answers. It lives in
[PumpAnalyst.cs](src/PumpAlarms.Web/PumpAnalyst.cs): `run_python` writes the code to `/root/step_N.py`,
runs it with a 60 s timeout, records code/stdout/stderr for the UI, and returns the output to the model.
The model is called through the OpenAI **Responses API** (`OpenAI.Responses.ResponsesClient`, `.AsIChatClient()`).

`/analyze` picks the mode from what is configured: model + sandbox → AI; sandbox only → the fixed
analysis from step 3; nothing → stub.

System prompt, short version:

> You are a maintenance analyst for water pumps. Answer using the alarm data only.
> Always compute numbers with `run_python`; never guess them. Finish with: the top 3 pumps
> that need attention, the evidence for each, and a suggested action.

| setting | value |
|---------|-------|
| `Foundry:Endpoint` | OpenAI v1 base URL, e.g. `https://<resource>.services.ai.azure.com/api/projects/<project>/openai/v1` |
| `Foundry:Deployment` | the model deployment name |
| `Foundry:ApiKey` | user-secrets locally, Container Apps secret in Azure |

**Model in another tenant (our setup):** the sandbox uses Entra auth in our tenant, the model uses an API key.

- Get the key in the model's tenant: Foundry resource → *Keys and Endpoint*. Key auth must not be disabled on it.
- If the project endpoint rejects the key (401), use the resource-level endpoint instead:
  `https://<resource>.services.ai.azure.com/openai/v1` (or `https://<resource>.openai.azure.com/openai/v1`).
- Locally, keep the key out of the repo (the project already has a `UserSecretsId`):

```powershell
dotnet user-secrets set "Foundry:ApiKey" "<key>" --project src/PumpAlarms.Web
```

> Keyless across tenants is possible later: a multi-tenant app registration that trusts the
> Container App's managed identity through a federated credential, with admin consent and the
> OpenAI User role in the model's tenant. Not worth it for the first version.

**Done when:** you upload the file, ask a question, and get an answer backed by code that ran in the sandbox.

### Tracing the sequence (no Aspire needed)

- **In the UI:** under the answer, a *Sequence* waterfall shows every span of that analysis: model calls
  (with tokens), `run_python` tool calls, sandbox file writes and execs (with exit code and the command).
  [TraceCollector.cs](src/PumpAlarms.Web/TraceCollector.cs) listens to the app's own `PumpAlarms.*`
  activity sources for the current request and returns them with the response.
- **In the console:** one log line per step (`Asking the model…`, `Model called run_python…`,
  `Sandbox write…`, `Sandbox exec… -> exit 0 in 1300 ms`, `Model answered after N code runs…`).
- **In the Aspire dashboard:** the spans are standard OpenTelemetry (GenAI conventions from
  `Microsoft.Extensions.AI` `.UseOpenTelemetry()`), set up in ServiceDefaults; see step 5.

---

## Step 5: Aspire, and deploy to Azure Container Apps

The solution is an Aspire app ([PumpAlarms.slnx](PumpAlarms.slnx)):

```
src/PumpAlarms.AppHost/          # describes the app, its settings and (for Azure) the ACA environment + identity
src/PumpAlarms.ServiceDefaults/  # OpenTelemetry (incl. PumpAlarms.* spans), health checks, service discovery
src/PumpAlarms.Web/              # the app
```

In Azure it is three pieces:

| piece | what it is | auth |
|-------|------------|------|
| **Container App** `pump-alarms` | the .NET app, standard Consumption environment, with the **Aspire dashboard** | user-assigned identity `pump-alarms-identity` |
| **ACA Sandbox** (existing, not managed by Aspire) | runs the Python the AI writes | the identity gets *Container Apps SandboxGroup Data Owner* on the group |
| **Foundry model** (other tenant) | `<deployment>` (a GPT-class model) | API key, an Aspire secret parameter → Container Apps secret |

**Why not Container Apps Express?** The app needs a managed identity (for the sandbox) and a secret
(for the Foundry key). Express lists both as *in development*.

### Settings

[AppHost.cs](src/PumpAlarms.AppHost/AppHost.cs) has parameters for the sandbox (`sandbox-*`) and the
model (`foundry-endpoint`, `foundry-deployment`, `foundry-key` as a secret). They live in the AppHost's
user-secrets as `Parameters:*`, next to the deployment target `Azure:SubscriptionId`, `Azure:Location`,
`Azure:ResourceGroup`. Nothing with IDs or keys is in the repo.

The ACA environment and the identity are only added in publish mode, so a local run needs no Azure provisioning.

### Run locally

```powershell
aspire run --apphost src/PumpAlarms.AppHost
```

The Aspire dashboard opens; each analysis is a trace with the model calls, `run_python` calls and
sandbox calls (in Development with prompts, code and output).

### Deploy

Needs Docker running (Aspire builds the image with the .NET SDK and pushes it to the registry it creates).

```powershell
aspire deploy --apphost src/PumpAlarms.AppHost
.\scripts\grant-sandbox-role.ps1     # once, after the first deploy
```

`aspire deploy` creates the registry, the Container Apps environment with the Aspire dashboard,
the identity `pump-alarms-identity` and the app (with `AZURE_CLIENT_ID` set, so `DefaultAzureCredential`
uses the identity). [grant-sandbox-role.ps1](scripts/grant-sandbox-role.ps1) then gives that identity
*Container Apps SandboxGroup Data Owner* on the existing sandbox group; Aspire does not manage that group,
and the role is assigned by name.

`appsettings.Development.json` is not published (`CopyToPublishDirectory="Never"`), and in Azure the app
runs as Production, so traces leave prompts and code out.

**Done when:** the public URL does the same as step 4, the sandbox badge shows the state, and the
traces show up in the Aspire dashboard of the Container Apps environment.

---

## Step 6: Extras (only if there is time)

- **Prepared disk image or snapshot** with pandas already installed, so every analysis gets a fresh
  sandbox that starts ready, with no install and no egress.
- **One sandbox per analysis:** create on upload, delete (or stop) when done.
- **Charts:** the model saves a PNG; the app reads it back (`aca sandbox fs cat` equivalent) and shows it.
- **Follow-up questions:** reuse the same sandbox, so the data is still there.
- **Show the isolation live:** ask the model to fetch `https://example.com` and watch it fail (egress blocked).

---

## Demo script for the talk (about 5 minutes)

1. Show the CSV. "Thousands of alarms, nobody reads these."
2. Upload it, ask *"Which pumps need attention?"*
3. While it runs, show the diagram: the code runs in the sandbox, not in the app.
4. Show the answer, then expand the code the model wrote.
5. Bonus: ask it to call the internet and show that it can't.

## Clean up

```powershell
aca sandbox delete -l name=pump-demo --yes
```
