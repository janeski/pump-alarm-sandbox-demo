# Pump Alarm Analyzer: architecture

How the demo is built: the parts, how they talk, who trusts whom, and why it is built this way.
For features, setup and the demo script see [README.md](README.md).

---

## 1. Goals and constraints

| | |
|---|---|
| **Goal** | A user uploads pump alarms and asks a question; an AI model answers by writing and running Python. |
| **Key rule** | Code written by the model is untrusted. It never runs in the web app, only in an isolated sandbox without internet access. |
| **Governance** | Every script is checked against a policy (Agent Governance Toolkit) before it may reach the sandbox, and every decision is audited. |
| **Constraint: two tenants** | The sandbox and the app are in tenant A; the model (`<deployment>` (a GPT-class model)) is in tenant B. |
| **Constraint: no secrets in the repo** | IDs and keys live in user-secrets locally and in Aspire parameters / Container Apps secrets in Azure. |
| **Constraint: preview APIs** | ACA Sandboxes is new; its data plane is only partly documented. The app uses the smallest verified surface. |
| **Audience** | A 5-minute live demo: everything the model does must be visible (code, output, timeline). |

---

## 2. System context

```mermaid
flowchart LR
    user(["User (browser)"])

    subgraph tenantA["Tenant A: the app and the sandbox"]
        subgraph cae["Container Apps environment (Consumption)"]
            app["pump-alarms<br/>ASP.NET Core .NET 10"]
            dash["Aspire dashboard"]
        end
        acr[("Container registry")]
        mi["Managed identity<br/>pump-alarms-identity"]
        subgraph sg["ACA Sandbox group <sandbox-group>"]
            dp["Sandboxes data plane<br/>management.swedencentral.azuredevcompute.io"]
            vm["Sandbox microVM<br/>Ubuntu, Python 3, pandas<br/>egress: Deny"]
        end
    end

    subgraph tenantB["Tenant B: the model"]
        foundry["Microsoft Foundry<br/>chat model deployment<br/>OpenAI v1 Responses API"]
    end

    user -- "HTTPS" --> app
    app -- "OTLP" --> dash
    acr -- "image pull" --> app
    mi -. "Entra token" .-> app
    app -- "REST, Bearer token" --> dp
    dp --> vm
    app -- "Responses API, API key" --> foundry
```

| Element | Responsibility | Technology |
|---------|----------------|------------|
| **pump-alarms** | UI, analysis loop, policy gate and audit, sandbox control, tracing | ASP.NET Core minimal API, static HTML/CSS/JS, Agent Governance Toolkit (`Microsoft.AgentGovernance`, in process) |
| **ACA Sandbox** | runs model-written Python in isolation | ACA Sandboxes (preview data plane `2026-02-01-preview`) |
| **Foundry model** | plans the analysis, writes Python, writes the answer | `<deployment>` (a GPT-class model), OpenAI v1 Responses API with tool calling |
| **Managed identity** | the app's identity towards the sandbox | user-assigned identity, role *Container Apps SandboxGroup Data Owner* |
| **Aspire dashboard** | traces, logs, metrics | `AspireDashboard` component of the Container Apps environment |
| **Container registry** | holds the app image | ACR, pulled with the environment's identity |

---

## 3. Inside the app

```mermaid
flowchart TB
    subgraph browser["Browser: wwwroot"]
        ui["index.html, app.js, app.css<br/>upload, question, answer, code runs,<br/>Sequence waterfall, sandbox badge"]
    end

    subgraph web["PumpAlarms.Web"]
        analyze["POST /analyze"]
        status["GET /sandbox<br/>POST /sandbox/stop, /sandbox/resume"]
        analyst["PumpAnalyst<br/>system prompt, run_python tool"]
        subgraph chat["IChatClient pipeline"]
            fic["FunctionInvokingChatClient<br/>max 5 tool rounds"]
            otel["OpenTelemetryChatClient<br/>source PumpAlarms.AI"]
            resp["ResponsesClient.AsIChatClient<br/>API key"]
        end
        gov["CodeGovernor<br/>CodeScanner + AGT GovernanceKernel<br/>governance/policy.yaml<br/>source PumpAlarms.Governance"]
        sandbox["SandboxClient<br/>status, exec, file write, stop, resume<br/>source PumpAlarms.Sandbox"]
        cred["DefaultAzureCredential<br/>managed identity in Azure, az login locally"]
        traces["TraceCollector<br/>spans of the current request"]
    end

    sd["PumpAlarms.ServiceDefaults<br/>OpenTelemetry, OTLP exporter, health checks"]

    ui --> analyze
    ui --> status
    analyze --> sandbox
    analyze --> analyst
    analyst --> fic --> otel --> resp
    fic -- "run_python(code)" --> analyst
    analyst -- "check(code, run)" --> gov
    analyst -- "only if allowed" --> sandbox
    status --> sandbox
    sandbox --> cred
    analyze --> traces
    web --> sd
```

| Component | File | Notes |
|-----------|------|-------|
| Endpoints | [Program.cs](src/PumpAlarms.Web/Program.cs) | picks the mode: model + sandbox → AI; sandbox only → fixed pandas script; nothing → stub data |
| Analysis loop | [PumpAnalyst.cs](src/PumpAlarms.Web/PumpAnalyst.cs) | one tool `run_python`; policy check first; output to the model capped at 8,000 characters; each run gets `timeout 60` |
| Governance | [Governance.cs](src/PumpAlarms.Web/Governance.cs), [policy.yaml](src/PumpAlarms.Web/governance/policy.yaml) | `CodeScanner` turns a script into facts; AGT `GovernanceKernel.EvaluateToolCall` decides; audit record per attempt; AGT audit events to the log |
| Sandbox client | [SandboxClient.cs](src/PumpAlarms.Web/SandboxClient.cs) | typed `HttpClient`; token cached until 5 minutes before expiry; rejects a non-data-plane URL |
| Trace collector | [TraceCollector.cs](src/PumpAlarms.Web/TraceCollector.cs) | an `ActivityListener` on `PumpAlarms.*`, keyed by trace ID, returned with the response |
| Service defaults | [Extensions.cs](src/PumpAlarms.ServiceDefaults/Extensions.cs) | traces only `/analyze` and `POST /sandbox/*`; no standard resilience handler (see decisions) |
| AppHost | [AppHost.cs](src/PumpAlarms.AppHost/AppHost.cs) | parameters; in publish mode also the ACA environment, dashboard and identity |

---

## 4. Runtime views

### 4.1 One analysis

```mermaid
sequenceDiagram
    autonumber
    actor U as Browser
    participant A as pump-alarms
    participant S as Sandbox data plane
    participant M as Model

    U->>A: POST /analyze (CSV, question)
    A->>S: GET sandboxes/{id}
    S-->>A: state
    opt not Running (auto-paused when idle)
        A->>S: POST sandboxes/{id}/resume
        loop until Running, max 30 s
            A->>S: GET sandboxes/{id}
        end
    end

    Note over A,S: upload as base64, 48 KB per command
    A->>S: executeShellCommand (append chunk to /root/pump_alarms.csv.b64)
    A->>S: executeShellCommand (base64 -d into /root/pump_alarms.csv)
    A->>S: executeShellCommand (head -n 6, wc -l)
    S-->>A: columns, first rows, line count

    A->>M: system prompt, preview, question, tool run_python
    loop until the model answers (policy allows 5 runs)
        M-->>A: run_python(code)
        A->>A: scan code into facts, AGT policy check, audit record
        alt allowed
            A->>S: executeShellCommand (write /root/step_N.py)
            A->>S: executeShellCommand (timeout 60 python3 /root/step_N.py)
            S-->>A: exitCode, stdout, stderr
            A->>M: exit code, stdout, stderr
        else blocked by a rule
            A->>M: BLOCKED by rule, what was detected, rewrite within the rules
        end
    end
    M-->>A: answer (markdown)
    A-->>U: answer, code runs, trace spans, audit trail
```

### 4.2 Sandbox status, pause and resume

```mermaid
sequenceDiagram
    actor U as Browser
    participant A as pump-alarms
    participant S as Sandbox data plane

    loop every 10 s (every 1 s while the state changes)
        U->>A: GET /sandbox
        A->>S: GET sandboxes/{id}
        S-->>A: state, stateDetails, lifecycle, resources
        A-->>U: Running / Stopped (idle or manual)
    end

    U->>A: POST /sandbox/stop (Pause button)
    A->>S: POST sandboxes/{id}/stop
    A-->>U: status, badge shows Pausing until Stopped

    U->>A: POST /sandbox/resume (Resume button)
    A->>S: POST sandboxes/{id}/resume
    A-->>U: status, badge shows Resuming until Running
```

The sandbox also pauses itself after 600 idle seconds (`lifecycle.autoSuspendPolicy`). Memory and disk are
kept, so the installed packages and uploaded files survive a pause.

---

## 5. Identity and trust

```mermaid
flowchart LR
    subgraph untrusted["Untrusted: model-written code"]
        code["Python in the sandbox microVM<br/>no egress, no credentials"]
    end

    subgraph appzone["Trusted: the app, tenant A"]
        app["pump-alarms"]
        policy["AGT policy gate<br/>pump-analyst-policy"]
        mi["pump-alarms-identity"]
        secret["Container Apps secret<br/>foundry--apikey"]
    end

    subgraph tenantB["Tenant B"]
        foundry["Foundry resource"]
    end

    sgroup["Sandbox group <sandbox-group>"]

    app -- "model code" --> policy
    policy -- "allowed code only" --> sgroup
    mi -- "SandboxGroup Data Owner" --> sgroup
    app -- "token from mi" --> sgroup
    sgroup -- "runs" --> code
    secret -- "API key" --> app
    app -- "key" --> foundry
    code -. "blocked: egress Deny" .-x foundry
```

| From → to | Authentication | Authorization |
|-----------|----------------|---------------|
| Browser → app | none (public demo) | — |
| App → sandbox data plane | Entra token, scope `https://management.azuredevcompute.io/.default`, from the user-assigned identity (`AZURE_CLIENT_ID`, `AZURE_TOKEN_CREDENTIALS=ManagedIdentityCredential`) | *Container Apps SandboxGroup Data Owner* on `<sandbox-group>` |
| App → Foundry (tenant B) | API key, Container Apps secret | key scope = the Foundry resource |
| App → registry | environment identity | AcrPull (set up by Aspire) |
| Sandbox code → anything | none | egress policy Deny, so no outbound calls at all |
| Local development | your `az login` for the sandbox, user-secrets for the key | your own role on the group |

**Two layers in front of the model's code.** First the policy gate in the app: a script that imports
network modules, starts processes, uses `eval`/`exec`, touches paths outside `/root`, is longer than
200 lines, or is run number 6 or later is blocked and never sent. Then the sandbox itself: even code that
passes the gate cannot reach the network (egress Deny) or anything outside its microVM. The gate is a text
scan, so it is a first filter and an audit point; the sandbox is the isolation boundary.

| Policy rule | Blocks |
|-------------|--------|
| `block-network` | `socket`, `ssl`, `requests`, `urllib`, `http`, `httpx`, `aiohttp`, `ftplib`, `smtplib`, … |
| `block-processes` | `subprocess`, `pty`, `os.system`, `os.popen`, `os.exec*`, `os.spawn*`, `os.fork` |
| `block-dynamic-code` | `eval(`, `exec(`, `compile(`, `__import__(`, `importlib` (not `df.eval` or `re.compile`) |
| `block-files-outside-root` | quoted absolute paths outside `/root` and `/tmp`, e.g. `'/etc/passwd'` |
| `limit-runs` | run 6 and later |
| `limit-code-size` | scripts over 200 non-empty lines |
| `allow-python-analysis` | allows the rest; anything else is denied by default |

**What the model's code can reach:** only the files in the sandbox (`/root/pump_alarms.csv` and its own
scripts). It holds no tokens or keys: the app sends commands to the sandbox, never the reverse.

**What leaves the app:** in Development the traces include prompts, code and output
(`EnableSensitiveData`); in Azure they don't.

---

## 6. Deployment view

```mermaid
sequenceDiagram
    actor Dev as Developer
    participant CLI as aspire deploy
    participant D as Docker (local)
    participant R as Container registry
    participant ARM as Azure Resource Manager
    participant S as grant-sandbox-role.ps1

    Dev->>CLI: aspire deploy --apphost src/PumpAlarms.AppHost
    CLI->>CLI: run AppHost in publish mode, generate Bicep
    CLI->>Dev: ask for parameters (cached in ~/.aspire/deployments)
    CLI->>ARM: registry, environment with Aspire dashboard, identity
    CLI->>D: build image with the .NET SDK
    D->>R: push pump-alarms image
    CLI->>ARM: container app pump-alarms (env vars, secret, identity)
    Dev->>S: once, after the first deploy
    S->>ARM: role assignment on <sandbox-group> for pump-alarms-identity
```

| Resource | Created by | Notes |
|----------|------------|-------|
| Container Apps environment + Aspire dashboard | `aspire deploy` | Consumption; Log Analytics workspace |
| Container registry | `aspire deploy` | pull with the environment identity |
| `pump-alarms-identity` | `aspire deploy` | name fixed in AppHost so the role script can find it |
| Container app `pump-alarms` | `aspire deploy` | port 8080, external ingress, min 1 replica |
| Role assignment on the sandbox group | `grant-sandbox-role.ps1` | the group is not managed by Aspire; role assigned by name |
| Sandbox group and sandbox | manually (portal / `aca` CLI) | pandas installed once in the sandbox |

---

## 7. Observability

| Signal | Where | What |
|--------|-------|------|
| **Sequence waterfall** | in the page, under each answer | spans from `PumpAlarms.AI` (model calls with tokens, tool calls), `PumpAlarms.Governance` (policy checks, red when blocked) and `PumpAlarms.Sandbox` (write, exec, wake up) |
| **Governance audit** | in the page (Governance panel), logs | per `run_python` attempt: time, run, SHA-256 of the code, decision, rule, what the scan found, exit code and sandbox time; AGT's own `PolicyCheck` / `ToolCallBlocked` / `PolicyViolation` events as log entries |
| **Traces** | Aspire dashboard (local and in ACA) | the same spans plus ASP.NET Core and HttpClient spans; GenAI semantic conventions |
| **Logs** | console, Aspire dashboard | one line per step: question, each `run_python`, each sandbox write/exec with exit code, final token counts |
| **Status** | sandbox badge | state, idle reason, CPU, memory, auto-pause interval |

Status polling (`GET /sandbox`), static files and health checks are not traced, so one trace is one
analysis or one pause/resume.

---

## 8. Configuration

| Parameter | Meaning |
|-----------|---------|
| `sandbox-management-url` | data plane, `https://management.<region>.azuredevcompute.io` |
| `sandbox-subscription-id`, `sandbox-resource-group`, `sandbox-group`, `sandbox-id` | which sandbox |
| `foundry-endpoint` | `https://<resource>.services.ai.azure.com/openai/v1` (resource level, for key auth) |
| `foundry-deployment` | `<deployment>` (a GPT-class model) |
| `foundry-key` | secret |

The AppHost maps them to `Sandbox__*` and `Foundry__*` environment variables. Values: AppHost user-secrets
for `aspire run`; asked and cached by `aspire deploy` (which runs as Production and ignores user-secrets).

---

## 9. Design decisions

| # | Decision | Why | Trade-off |
|---|----------|-----|-----------|
| 1 | Run model code in an **ACA Sandbox**, not in the app | model output is untrusted; microVM isolation, egress Deny, pause when idle | an extra service and a preview API |
| 2 | Call the sandbox **data plane over REST**, not the `aca` CLI | a CLI cannot run inside the container; one `HttpClient` is easy to explain | the contract was read from CLI debug output |
| 3 | Upload files through **`executeShellCommand` + base64** | the file API is not documented; exec is verified | many small calls for big files (48 KB each) |
| 4 | **One tool**, `run_python`, max 5 rounds | simple to explain and to show; the model can fix its own errors | no charts or file download yet |
| 5 | **Responses API** via `Microsoft.Extensions.AI` | function calling and OpenTelemetry for free; matches the Foundry endpoint | the OpenAI SDK marks it experimental (`OPENAI001`) |
| 6 | **API key** for the model | the model is in another tenant; a managed identity only gets tokens in its own tenant | a secret to rotate; keyless needs a cross-tenant app registration |
| 7 | **Managed identity** for the sandbox | no secret; role scoped to one sandbox group | role assignment is a separate step (not in Aspire) |
| 8 | Standard **Container Apps**, not Express | Express lacked managed identity and secrets | slightly more to set up |
| 9 | **Aspire** for local run and deploy | one model for local and Azure; dashboard and traces built in | `aspire deploy` caches answers; Production ignores user-secrets |
| 10 | **No standard resilience handler** | its 10 s attempt timeout and retries would cut off and repeat sandbox commands | no automatic retries for transient errors |
| 11 | Trace waterfall **in the UI** | the audience sees the sequence without opening another tool | in-memory collection per request |
| 12 | **One shared sandbox** | fast to start, packages installed once | users share files; not for production |
| 13 | **Agent Governance Toolkit policy gate** before the sandbox | stops unwanted code before it leaves the app, audits every attempt, policy in YAML that people can read | one more moving part; a text scan can be fooled, so the sandbox stays the boundary |
| 14 | **Scanner facts + simple AGT rules** | AGT conditions support booleans, comparisons, `in` and `and`/`or`, but no substring checks; facts like `imports_network` keep the YAML readable | the module lists live in code ([Governance.cs](src/PumpAlarms.Web/Governance.cs)), the decisions in YAML |
| 15 | Run limit in the **policy**, tool loop allows a few extra rounds | the limit is governed and audited, and the model can still answer after a block | the model may try once more after the limit |

---

## 10. Limits and next steps

- **One sandbox per analysis**: create from a prepared disk image (pandas preinstalled), delete afterwards.
- **Verify stop/resume** against the CLI's traffic, as was done for status and exec.
- **Charts**: let the model save a PNG and read it back through the sandbox.
- **Keyless model access**: multi-tenant app registration trusting `pump-alarms-identity` through a federated credential.
- **Streaming progress**: server-sent events so the pipeline strip lights up step by step.
- **Authentication** on the app itself before it leaves demo use.
