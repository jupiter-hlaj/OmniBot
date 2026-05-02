# Sotto Deploy Pipeline

Sotto's OmniBot fork deploys via the `Build On Demand` GitHub Actions workflow at `.github/workflows/build-on-demand.yml`. This doc covers the trigger, the deploy flow, the cluster-readiness pre-check, and the recovery path when something fails.

## How it deploys

**Trigger:** `push` to `main` when files change under `src/**`, `build/**`, `scripts/**`, or in either of the workflow files (`build-on-demand.yml`, `routine-buildimage.yml`). Also exposed as `workflow_dispatch` for manual re-runs.

**Two-job pipeline:**

1. **`build`** (calls `routine-buildimage.yml`) — builds the Windows container on `windows-latest`, tags it with a content-derived hash, pushes to `ghcr.io/jupiter-hlaj/omnibot`. The "Check if Docker image exists" step skips the rebuild if the tag already exists in the registry.

2. **`deploy`** (auto-runs after build, OIDC-authed to Azure) — rolls the new image onto the AKS StatefulSet `sotto-bot` in cluster `sotto-aks-central` (resource group `SOTTO-AKS-RG-CENTRAL`). Steps:
   1. `Azure login (OIDC)` — federated identity, no stored creds
   2. `Pre-check — AKS cluster must be running` *(see below)*
   3. `Roll image via az aks command invoke` — runs `kubectl set image statefulset/sotto-bot recording-bot=<new-image> -n sotto-bot` against the cluster's admin context
   4. `Watch rollout (15 min cap)` — `kubectl rollout status` polling, bounded so a slow rollout doesn't hang the workflow forever (Windows pods pull multi-GB images, ~5-10 min per pod)
   5. `Final pod status` — diagnostic `kubectl get pods` for the run logs

The agent's deploy workflow is just `git push`. No manual `az`, `kubectl`, or `helm` from a developer machine.

## The cluster-readiness pre-check

**What it does:** queries the cluster's `powerState` via `az aks show` once. If `Running`, proceeds. If anything else, exits non-zero with a GH Actions error annotation naming the cluster, its current state, and the command needed to start it.

**Why single-shot, not polling:**

A stopped AKS cluster is a *human-actionable* state — somebody has to start it. Polling for minutes doesn't summon a human; it just delays the actionable signal. The right pattern is to fail immediately with a clear message so the deploy log surfaces the action required, not buries it behind a 15-minute timeout.

The "transient race" case (cluster mid-startup right when the push lands) is rare for this project — cluster start/stop is a deliberate cost-control action, not an automatic event. The common cases are "cluster is up" (pre-check passes, deploy proceeds) and "cluster is down" (pre-check fails fast, human starts it, push retriggers). Optimizing for the rare race at the cost of a slow signal in the common failure case is the wrong trade.

**What it doesn't do:** it does NOT start the cluster. Cluster lifecycle is owned by the operator, not the deploy pipeline. A pipeline that auto-starts infrastructure makes cost-control work invisible and erodes the discipline of explicit start/stop.

**Failure output looks like:**

```
::error title=AKS cluster not running::Cluster 'sotto-aks-central' is in PowerState=Stopped, not Running.
::error::Start it: az aks start --name sotto-aks-central --resource-group SOTTO-AKS-RG-CENTRAL
::error::Then re-run this workflow (or push another commit).
```

GH Actions surfaces these as red error annotations on the run summary, so the failure cause and remediation step are visible without reading the full step log.

## Recovery from a failed deploy

The workflow does **not** auto-retry. A failed deploy on an existing commit stays failed until something fires the workflow again. To recover:

1. Fix the underlying cause (commonly: start the AKS cluster).
2. Push another commit. Either a real change you needed to ship anyway, or `git commit --allow-empty -m "redeploy"` if the prior commit's code is still what should ship.

The workflow re-fires on the new push. The build job's image-exists check will detect the prior build's image in GHCR and skip the rebuild, so the deploy starts within seconds. The pre-check now passes (cluster is running), the rollout proceeds, the StatefulSet picks up the new image.

`gh workflow run` and `gh run rerun --failed` both work as manual triggers but are not the standard path — they bypass the "deploys come from `git push`" discipline. Use them only when an empty-commit retrigger is genuinely not appropriate.

## What the pipeline does NOT cover

- **Tests.** Build On Demand does not run unit tests. Tests run via the separate `Continuous Integration` workflow, which has a different trigger and is currently not exercising this repo's tests due to a path-filter bug (`paths: src` instead of `src/**`). Logging this here so it's visible until that's fixed.
- **Migration of long-lived bot state.** Pods come up with the new image; in-flight calls on the prior pod are handled by the `terminationGracePeriodSeconds` + `preStop` hook described in `aks.md` (the upstream OmniBot deploy doc).
- **Cluster start/stop.** Operator action, not pipeline action.
