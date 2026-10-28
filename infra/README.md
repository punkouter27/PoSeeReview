# Azure infrastructure

Bicep for PoSeeReview. **Applied by hand, never by CI** — `.github/workflows/deploy.yml` only
publishes the app package to the existing App Service `app-poseereview2` (RG `PoSeeReview`).

| File | What it is |
|---|---|
| `main.bicep` | Subscription-scope template: resource group, monitoring, storage, App Service, budget, and read access to the shared vault |
| `alerts.bicep` | Alerts for the **live** app, deployed on its own (see below) |
| `modules/appservice.bicep` | Linux App Service, .NET 10, system-assigned identity, startup command |
| `modules/storage.bicep` | Storage account (Tables + Blobs). The app reaches it by Managed Identity; the data-plane role grant is not in this template |
| `modules/monitoring.bicep` | Log Analytics + Application Insights |
| `modules/keyvaultaccess.bicep` | `Key Vault Secrets User` role for the app's identity |
| `modules/budget.bicep` | Monthly budget alert |

## Secrets

Secrets live in the shared vault **`kv-poshared2`** (RG `PoShared`) under the `PoSeeReview--` prefix.
The templates never create or write a vault; they only grant the app's identity read access.

```powershell
az keyvault secret set --vault-name kv-poshared2 --name "PoSeeReview--GoogleMaps--ApiKey" --value "<key>"
```

## Provision

```powershell
az deployment sub create -l westus2 -f infra/main.bicep -p environmentName=dev location=westus2
```

## Alerts

```powershell
az deployment group create -g PoSeeReview -f infra/alerts.bicep -p alertEmail=<address>
```

Deployed 2026-10-21. Every rule emails the `ag-poseereview-email` action group.

| Rule | Fires when | Sev |
|---|---|---|
| `poseereview-http5xx` | more than 5 5xx responses in 5 minutes | 1 |
| `poseereview-generation-failures` | more than 10 failed generations in an hour — the comic stream is a 200 that reports errors in-band, so Http5xx never sees them | 1 |
| `poseereview-ai-spend` | images or chat spend in 24h above the thresholds | 2 |
| `poseereview-fail-open` | the generation budget or the moderation gate let a request through unchecked | 2 |
| `poseereview-image-refusals` | more than 5 image-model refusals in 24h | 3 |
| `poseereview-moderation-waiting` | a comic was flagged by the pre-publish screen or auto-hidden by reports | 3 |

Log alerts that mute their actions (`muteActionsDuration`) must set `autoMitigate: false`; Azure
rejects the rule otherwise.

## Validate

```powershell
az bicep build -f infra/main.bicep --stdout > $null
az bicep build -f infra/alerts.bicep --stdout > $null
```
