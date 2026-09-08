# Work PC handoff

All deployment work takes place on the work PC using the existing build-deploy GitHub Action, JFrog, Helm and Argo configuration. This repository does not provision or modify those systems. An `origin` remote is configured; this implementation task has not pushed changes. Final release commits and the tag are kept local for the approved transfer/publishing workflow.

## Transfer and configuration

The local `v0.1.0` tag identifies the tested milestone. Copy the generated `artifacts/SQSSenderSMPP-0.1.0.bundle` through an approved transfer mechanism; it contains source, commits and tags, not local state or dependencies.

```powershell
git clone 'C:/transfer/SQSSenderSMPP-0.1.0.bundle' SQSSenderSMPP
cd SQSSenderSMPP
git switch main
# Once the approved GitHub repository is available, set origin there on the work PC.
```

Alternatively push this local history to the approved repository from an authorized environment and clone/pull it normally. No GitHub repository or remote address was invented here.

1. Install the pinned SDK and restore through the existing JFrog NuGet proxy. Use an ignored `NuGet.Work.Config` (`dotnet restore --locked-mode --configfile NuGet.Work.Config`) or replace the public source according to repository policy. Credentials belong in the platform credential mechanism.
2. Reconcile the producer payload with `contracts/sms-envelope.schema.json`. Stable `smsId` is per logical SMS, never the SQS receipt handle. Add a reviewed versioned translation if the existing work payload differs; do not guess its field mapping.
3. Reconcile the existing DynamoDB table with the storage contract: string partition key `pk`, no sort key, numeric `expiresAt` TTL. Both submission and DLR items use this table. Retain records beyond all permitted replay/DLR windows and enable the approved PITR/encryption/capacity policies. The sender never creates the table.
4. Copy `local/work-config.example.json` to an ignored `appsettings.Work.json` and replace placeholders using existing configuration. `SENDER_CONFIG_FILE` accepts an absolute path. Environment variables (`Aws__QueueUrl`, `Smpp__Host`, `Sender__PerBindTps`, etc.) and command-line options override the file. Restart for configuration/secret changes; live rotation/rebinding is not implemented in 0.1.0.
5. Set `DOTNET_ENVIRONMENT=Production`. Use the AWS default credential chain/Pod Identity. No AWS access keys are configured by the runtime. Mount the SMPP password and Inetlab license as files through the existing secret mechanism. Confirm the license and actual SMPP version before a live bind.
6. Align per-bind TPS/window, source TON/NPI, segmentation, retry/permanent status lists, TLS and DLR message-ID behavior with the carrier profile. Example values are local starting points only. Replica count multiplied by per-bind TPS must stay within the carrier aggregate allocation.

Required runtime IAM actions are source-queue `sqs:ReceiveMessage`, `sqs:DeleteMessage`, `sqs:ChangeMessageVisibility`, and ledger `dynamodb:GetItem`/`dynamodb:PutItem`, plus applicable KMS/secret/telemetry access through the existing platform. Runtime needs no CreateTable/CreateQueue, DLQ replay or infrastructure privileges.

## Existing build-deploy action

`.github/workflows/ci.yml` is a portable build/test-only workflow. It has read-only repository permissions and no image publication, cloud login or deployment step. Integrate its checks into the existing enterprise action and replace public feeds/runners with work equivalents as needed.

Run the same gates before packaging: locked restore, formatting, unit tests, local integration, standalone smoke and Release publish. Work CI then performs its existing Xray/license checks, SBOM/provenance/signing, immutable JFrog promotion and approved Helm/GitOps update. Argo remains the deployment reconciler.

The supplied Dockerfile requires two approved **digest-pinned** base-image build arguments and a BuildKit NuGet config secret:

```text
docker build --build-arg SDK_IMAGE=<approved-dotnet-10-sdk@sha256:digest> \
  --build-arg RUNTIME_IMAGE=<approved-dotnet-10-aspnet@sha256:digest> \
  --secret id=nuget_config,src=NuGet.Work.Config -t <local-build-tag> .
```

Use ASP.NET runtime images because the worker hosts health endpoints. The SDK image must satisfy `global.json`; runtime image must provide .NET 10, ICU/timezone data required by the chosen carrier profile, and `APP_UID`. Runtime listens on 8080 and uses the non-root image account. Configure read-only root filesystem, secret mounts, resources and egress in the existing chart. Image builds were not executed on this PC because Docker is absent.

Existing Helm configuration should map readiness to `/health/ready`, liveness to `/health/live`, permit at least 120 seconds for termination (105-second drain plus final persistence/unbind), and retain one bind per pod. Keep KEDA warm replicas, max binds and rollout surge within the carrier allocation.

## Reconciliation and replay

An `Unknown` row is a durable quarantine, even if its envelope was deleted. Do not delete its ledger key or replay it blindly. Reconcile accepted segment IDs with the carrier, record the approved business decision, then use a separately reviewed conditional repair/replay tool. A `RetryExhausted` row retains its envelope for native DLQ redrive and also requires a controlled policy change before another attempt. No automatic repair tool is included.

When changing the library, encoding policy or concatenation profile, drain unresolved multipart work first or provide a migration. The stored profile hash rejects resuming a different wire plan under an existing SMS identity.

Complete the qualification gates in `docs/implementation.md` and `docs/production-readiness-checklist.md` before declaring a production release. SemVer 1.0 is reserved for that milestone.
