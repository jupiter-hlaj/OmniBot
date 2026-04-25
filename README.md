# Sotto Teams Recording Bot

Sotto's compliance recording bot for Microsoft Teams. Runs on AKS (Windows Server 2022 node pool), captures per-speaker PCM audio via the Skype.Bots.Media SDK, and uploads a stereo WAV to S3 when each call ends.

Forked from [LM-Development/OmniBot](https://github.com/LM-Development/OmniBot) (MIT).

---

## How it works

1. A compliance recording policy (`SottoRecording`) is assigned to recorded agents in the customer's M365 tenant via Engine B (Azure Automation PowerShell runbook).
2. When a recorded agent makes or receives a Teams call, Microsoft automatically invites this bot.
3. The bot streams audio in real time via `Skype.Bots.Media`. Each `UnmixedAudioBuffer` entry (one per active speaker) is written into a channel-aware `AudioBuffer`.
4. When the call ends, `CallHandler` builds a stereo WAV and uploads it to `sotto-recordings-{account_id}` S3, then enqueues a message to `sotto-call-events` SQS.
5. The Sotto backend pipeline picks it up from there: WhisperX transcribes, Bedrock generates notes, WebSocket pushes to Cockpit.

---

## Infrastructure

| Item | Value |
|---|---|
| AKS cluster | `sotto-aks-central` |
| Namespace | `sotto-bot` |
| Helm release | `sotto-bot` |
| Image registry | `ghcr.io/jupiter-hlaj/omnibot` |
| Bot endpoint | `https://bots.sotto.cloud:9441/api/calling` |
| Azure Bot Service | `SottoTeamsBot` in `SOTTO-BOT-RG-CENTRAL` |
| AAD app client ID | `27fed775-5fc6-43a3-8fad-3fbf63e0347e` |

---

## Deploy pipeline

### 1. Trigger Build On Demand

```bash
cd /path/to/omnibot-fork
gh workflow run build-on-demand.yml
```

Runs on a `windows-2022` GitHub Actions runner. Computes a content hash of `src/`, `build/`, `scripts/`. Skips the build if an image with that hash already exists in GHCR. Takes ~25-30 minutes when a full build is required.

Monitor:
```bash
gh run list --limit 5
gh run view <run-id>
```

### 2. Get the image tag

```bash
gh run view --job=<job-id> --log | grep "Successfully tagged"
```

### 3. Helm upgrade

```bash
cd deploy/teams-recording-bot
helm upgrade sotto-bot . \
  --namespace sotto-bot \
  --reuse-values \
  --set image.tag=<new-tag> \
  --wait --timeout 15m
```

Prerequisites: `kubectl` context set to `sotto-aks-central`, `helm` installed, `gh` CLI authenticated.

---

## Sotto integrations

Sotto business logic lives in `src/RecordingBot.Services/Sotto/` and `src/RecordingBot.Services/Bot/`:

| Component | Purpose |
|---|---|
| `AudioBuffer` | Channel-aware PCM accumulator. Spills to disk at 40 MB. Builds stereo WAV on call end. |
| `AwsUploader` | Multipart S3 upload of the stereo WAV. |
| `DynamoResolver` | Resolves `ms_tenant_id` + `ms_user_id` to Sotto `tenant_id` + `agent_id` via DynamoDB GSIs. |
| `CallHandler` | Orchestrates call lifecycle: join, record, terminate, upload, enqueue. |
| `BotMediaStream` | Reads from `UnmixedAudioBuffers` (per-speaker PCM). Mixed stream is always silence in compliance recording mode. |

---

## Known issues

**Graceful shutdown not implemented.** The bot does not handle `SIGTERM`. Rolling updates require force-deleting stuck terminating pods. Active calls at the time of pod replacement lose the tail end of their audio. Do not ship to real customer tenants until this is resolved. See `docs/c1-aks-migration.md` for details.
