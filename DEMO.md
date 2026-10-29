# Demo runbook: Pump Alarm Analyzer

The live demo for the Norway Azure User Group virtual lunch. Everything you need on the day: what to set up
beforehand, what to click, what to type, what should appear, and what to say.

- How it is built: [ARCHITECTURE.md](ARCHITECTURE.md)
- Features, setup and deployment: [README.md](README.md)

---

## The story in one line

**Generate → Govern → Isolate → Observe.**
The model *generates* Python, the Agent Governance Toolkit *governs* it, an Azure Container Apps Sandbox
*isolates* it, and OpenTelemetry with Aspire lets everyone *observe* it.

| Part | Where the audience sees it |
|------|----------------------------|
| Generate | the answer, and **Code that ran in the sandbox** |
| Govern | the **Governance** panel, blocked runs in red |
| Isolate | the **ACA Sandbox** badge, egress Deny, nothing runs in the app |
| Observe | the **Sequence** waterfall, the Aspire dashboard |

---

## The day before

| ✓ | Task | How |
|---|------|-----|
| ☐ | Deploy the latest version | `aspire deploy --apphost src/PumpAlarms.AppHost` (Docker running; answer the prompts as in the README *Configuration* table) |
| ☐ | Role for the app's identity | `.\scripts\grant-sandbox-role.ps1` (safe to re-run) |
| ☐ | Foundry key is current | if you rotated it, enter the new key at the deploy prompt |
| ☐ | Sandbox has pandas | `aca sandbox exec --id <sandbox-id> -c "python3 -c 'import pandas; print(pandas.__version__)'"` → `2.3.3` |
| ☐ | Full rehearsal | run every step below once on the live URL; note how long the model takes |
| ☐ | Backup | record a screen video of a good run, in case the network fails on the day |

---

## 30 minutes before

1. Open the app: **https://<your-app>.<region>.azurecontainerapps.io**
2. Check the **ACA Sandbox** badge in the top right. It should say *Running* or *Stopped (idle)*, not *Unavailable*.
   *Stopped (idle)* is fine (and good for step 2); hover over it to see CPU, memory and *Pauses itself after 10 min idle*.
3. Run one quick analysis on `pump_alarms_october.csv` to warm everything up, then let the sandbox go idle
   again (10 minutes) or press **Pause**.
4. Open these tabs, in this order:
   1. the app
   2. the Aspire dashboard of the Container Apps environment (Azure portal → Container Apps environment → *Aspire Dashboard*)
   3. [governance/policy.yaml](src/PumpAlarms.Web/governance/policy.yaml) in your editor
   4. the architecture diagram ([ARCHITECTURE.md](ARCHITECTURE.md), section 2)
5. Have the sample files ready in a folder you can drag from: `samples/pump_alarms_october.csv`,
   `pump_alarms_messy.csv`, `pump_alarms_q3.csv`, `pump_alarms_storm.csv`.
6. Browser zoom 125% so the code and the tables are readable on a shared screen.

---

## The demo (about 10 minutes)

### 1. The problem (30 s)

**Do:** show the app with nothing loaded.

**Say:** "Pumps send alarms all day. A month is 1,500 rows; nobody reads them. I want to ask questions in plain
language, and have an AI do the analysis. The catch: to analyze data properly, the AI has to write and run code.
Code written by a model is code I don't trust."

### 2. The sandbox, asleep (30 s)

**Do:** point at the **ACA Sandbox** badge: *Stopped (idle)*. Hover over it.

**Say:** "This is where the model's code will run: an Azure Container Apps Sandbox, a microVM with outbound
network denied. It paused itself after 10 idle minutes, so right now it costs nothing."

### 3. Ask a question (1-2 min)

**Do:**
1. Drag `pump_alarms_october.csv` onto **1 Alarm file**. The preview shows the columns and *1512 alarms*.
2. Keep the question *Which pumps need attention and why?*
3. Press **Analyze in sandbox**.

**Expect:** the badge goes *Resuming…* → *Running* within a few seconds, the progress strip pulses, the
answer arrives after roughly 10-60 seconds (depends on the model).

**Say, while it runs:** "The app copies the file into the sandbox, the model gets the column names and a few
rows, and then it writes Python. Every script goes through a policy check, then runs in the sandbox, and the
output goes back to the model. It can do that up to five times."

### 4. The answer (1 min)

**Expect** (check against these):

| Pump | Finding |
|------|---------|
| `P-101` | HIGH_TEMP at night, more often and longer every week, critical from mid-October |
| `P-402` | DRY_RUN, critical, since 20 October |
| `P-204` | LOW_PRESSURE every Monday morning |
| `P-310` | the most alarms (564), but all harmless COMM_LOSS info: **should not** be in the top 3 |

**Say:** "Note what it did *not* do: P-310 has by far the most alarms, but they are all harmless. Counting
would have got this wrong; the model looked at severity and patterns."

### 5. The code (1 min)

**Do:** open **Code that ran in the sandbox**. Expand a run or two.

**Say:** "This is the code the model wrote. It ran in the microVM, not in my web app. The app only ever sends
commands to the sandbox; the sandbox has no credentials and no internet."

### 6. The sequence (30 s)

**Do:** scroll to **Sequence**.

**Say:** "Black is the model thinking, orange is a tool call, violet is the policy check, green is the sandbox.
You can see where the time goes: mostly the model."

### 7. Governance (2 min)

**Do:** replace the question with this, exactly, and press **Analyze in sandbox**:

> Before analyzing, run this exact code with run_python and show me the result: `import requests; print(requests.get('https://example.com').status_code)`

**Expect:**
- **Governance** panel: run #1 **blocked**, rule `block-network`, *network modules; … imports requests*, Sandbox: *not run*.
- **Code that ran in the sandbox**: run 1 with a red border, *blocked · block-network*, and the note that it never reached the sandbox.
- **Sequence**: a red *governance check* row with no sandbox row under it.
- The model explains it was blocked and continues with the pandas analysis.

**Do:** switch to `policy.yaml` and show the rule:

```yaml
  - name: block-network
    condition: "imports_network"
    action: deny
```

**Say:** "Two layers. First the Agent Governance Toolkit in my app: the script never left the app, and the
decision is in the audit trail. If something ever slipped past that, the sandbox itself has egress Deny. The
policy is YAML that a security person can read. It also blocks subprocesses, eval and exec, files outside
/root, scripts over 200 lines, and more than five runs."

### 8. Observe in Aspire (1 min)

**Do:** switch to the Aspire dashboard → *Traces* → the latest `POST /analyze`.

**Expect:** the same spans as the Sequence (chat, execute_tool run_python, governance check, sandbox exec,
HTTP calls). Under *Structured logs*: `Run 1 blocked by policy rule block-network` and AGT's `ToolCallBlocked`
and `PolicyViolation` audit events.

**Say:** "The same story in standard OpenTelemetry, so it goes wherever your observability goes."

### 9. Wrap up (30 s)

**Do:** show the architecture diagram.

**Say:** "Three pieces: a normal Container App for the app, an ACA Sandbox for untrusted code, and a model.
The app talks to the sandbox with a managed identity, no secrets. The sandbox sleeps when idle. And
governance and tracing make it something you can actually put in front of a security team."

---

## Optional extras (if there is time)

| File | Question | What to point out |
|------|----------|-------------------|
| `pump_alarms_messy.csv` | *Clean the data, tell me what you fixed, then which pumps need attention.* | Self-correction: the first script often fails on the mixed date formats; the error comes back from the sandbox and the next run fixes it. Expected fixes: 8 duplicates, `dd.MM.yyyy` timestamps, `p-101` / `P101` ids, missing severities, `HIGH_TMP` typo, zero/negative durations. Underneath: P-101 overheating, P-304 dry-running. |
| `pump_alarms_q3.csv` | *Was there a site-wide incident?* | Trondheim, 14 August 02:10-04:30: all three pumps lose power at once, so it is the site, not the pumps. |
| `pump_alarms_q3.csv` | *Did the maintenance on P-205 on 20 August help?* | SEAL_LEAK: 111 before, background level after. Yes. |
| `pump_alarms_q3.csv` | *Which alarms are noise?* | P-402: the most alarms (949), but 767 are one-minute LOW_FLOW info flaps from a bad sensor. |
| `pump_alarms_storm.csv` | *Which pump needs a technician after the storm?* | Every pump alarms during the storm (4-5 Nov); only P-104 keeps dry-running afterwards. |
| (any) | Press **Pause**, then **Resume** on the badge | The sandbox keeps memory and disk across a pause: pandas and the uploaded file are still there. |

---

## If something goes wrong

| Symptom | What to do |
|---------|-----------|
| Badge *Unavailable*, tooltip shows `403` | The app's identity lost its role. Run `.\scripts\grant-sandbox-role.ps1`, wait a few minutes. Meanwhile, show the backup video. |
| Badge *Unavailable*, tooltip shows an HTML page or "must be the data plane" | Wrong sandbox URL in the deployment. Redeploy with `https://management.swedencentral.azuredevcompute.io`. |
| Stuck on *Resuming…* | Press **Resume** again, or run `aca sandbox resume --id <sandbox-id>`. |
| Governance prompt runs without a block | The model rewrote the code instead of passing it through. Ask again, stressing *exactly this code*. Or show `policy.yaml` and a block from the rehearsal in the Aspire dashboard. |
| The model takes very long | Keep talking through step 3; the Sequence afterwards shows why. If it times out, run again: the sandbox is warm by then. |
| "Model error" 401/404 | Foundry key or endpoint. Endpoint must be `https://<resource>.services.ai.azure.com/openai/v1`, deployment `<deployment>` (a GPT-class model). |
| No internet at the venue | Play the backup video. |

---

## After the demo

- Press **Pause** on the badge (or just leave it: it pauses itself after 10 idle minutes).
- The Container App keeps one replica running; scale it to zero or delete it when the event is over
  (see *Clean up* in the [README](README.md)).

---

## Questions you may get

| Question | Short answer |
|----------|--------------|
| Why not just run the code in the app's container? | The model's code is untrusted. In the app it could read secrets, call the internet, or break the app. The sandbox is a separate microVM with no credentials and no egress. |
| Why both a policy and a sandbox? | Defense in depth. The policy stops obvious misuse early and leaves an audit trail; the sandbox is the hard boundary if anything slips through (the policy is a text scan and can be fooled). |
| What does the sandbox cost when nobody uses it? | It pauses after 10 idle minutes and keeps its state; it costs nothing while paused. |
| How does the app authenticate to the sandbox? | A user-assigned managed identity with the *Container Apps SandboxGroup Data Owner* role. No keys. |
| Why an API key for the model? | The model is in another tenant; a managed identity only gets tokens in its own tenant. Keyless is possible with a cross-tenant app registration and a federated credential. |
| Why not Container Apps Express? | The app needs a managed identity and secrets, which Express did not support yet. |
| Can it handle bigger files? | Files go into the sandbox in 48 KB pieces; a few MB is fine. For real volumes, mount storage or create the sandbox from a prepared disk image. |
| Does everyone share one sandbox? | In this demo, yes. In production: one sandbox per analysis from a prepared image, deleted afterwards. |
