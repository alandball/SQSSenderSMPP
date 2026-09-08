# Enterprise SQS-to-SMPP sender

This repository implements a .NET 10 SMS submission worker that consumes Amazon SQS envelopes containing up to 50 logical SMS messages, uses a conditional DynamoDB ledger, and submits via one Inetlab SMPP transceiver session per process. Version **0.1.0** runs locally with emulated AWS services and an SMPP test server. The vendor's actual SMPP version is still unconfirmed; 3.4 is the local test profile.

## Run locally

The current PC is configured. From this directory:

```powershell
pwsh ./build.ps1 test
pwsh ./build.ps1 integration
pwsh ./build.ps1 smoke
# Stop the local processes when finished:
pwsh ./build.ps1 integration-down
```

For a new checkout, first run `pwsh ./build.ps1 setup -PythonPath <Python-3.12+-executable>`.

- [Local setup and commands](docs/local-development.md)
- [Implemented behavior and qualification gaps](docs/implementation.md)
- [Work PC / existing build-deploy handoff](docs/work-pc-handoff.md)
- [AI development workflow](docs/ai-workflow.md)
- [Local validation evidence](docs/validation.md)
- [Changelog](CHANGELOG.md)

The architecture below remains the production target. Work SQS, JFrog, Argo, Helm and deployment configuration are external to this local environment.

## Executive decision

- Build the worker on **.NET 10 LTS** and isolate the SMPP dependency behind an internal adapter. Qualify **Inetlab.SMPP** as the preferred supported implementation; use Java 25/21 plus jSMPP as the open-source fallback if commercial licensing is not approved.
- Run **one `bind_transceiver` session per pod**. A pod is the failure, scaling, rate-limit, health, and rollout unit for one session. Multiple binds per pod are an exception that requires benchmark evidence and a new ADR.
- Deploy across **three Availability Zones** and provision enough warm sessions to continue the target rate after losing one AZ. Do not scale to zero.
- Consume the source queue with 20-second long polling and bounded prefetch. Expand each envelope into child work, but delete the SQS message only when all children are durably terminal at the submission stage.
- Use a **DynamoDB submission ledger** keyed by stable per-SMS IDs. It suppresses known replays and preserves partial progress when an envelope is redelivered or replayed from the DLQ.
- Be explicit that SQS plus SMPP cannot provide atomic exactly-once delivery. A timeout after the SMSC accepted a request creates an `UNKNOWN` outcome. Quarantine/reconcile it unless the business explicitly accepts duplicate-risk retries.
- Scale with **KEDA on SQS backlog**, constrained by carrier bind/TPS limits and long scale-down stabilization. Use scheduled pre-scaling for known daytime campaigns.
- Use **Terraform** for AWS infrastructure, **Helm** for Kubernetes packaging, **Argo CD** for GitOps deployment, **JFrog Artifactory/Xray** for immutable image/chart promotion and scanning, and **Rancher** for fleet visibility, policy, and operator access. Keep their ownership boundaries separate.

## Capacity headline

The real-world peak is the governing requirement:

```text
50,000 SQS envelopes × 50 logical SMS ÷ 1,200 seconds
= 2,083 logical SMS/second average
```

The initial engineering target is **3,000 logical SMS/s**, then multiplied by the measured average number of SMPP segments per logical SMS. For example, an average of 1.2 segments requires **3,600 `submit_sm` PDUs/s**. The carrier's aggregate TPS, per-bind TPS, maximum simultaneous binds, permitted outstanding window, and response latency must support that number; Kubernetes cannot compensate for a lower carrier contract.

## Contents

- [Target architecture](docs/architecture.md)
- [Capacity model and service objectives](docs/capacity-and-slos.md)
- [Platform and delivery model](docs/platform-and-delivery.md)
- [Local and production test strategy](docs/testing.md)
- [Production readiness checklist](docs/production-readiness-checklist.md)
- [ADR-0001: runtime and SMPP library](docs/decisions/0001-runtime-and-smpp-library.md)
- [ADR-0002: one bind per pod](docs/decisions/0002-one-bind-per-pod.md)
- [Versioned SQS envelope contract](contracts/sms-envelope.schema.json)

## Status

The local sender and test harness are implemented. This implementation task has not pushed changes or modified work infrastructure. Production deployment and capacity commitments remain gated by the SMSC interface profile, commercial licensing, existing work configuration and the qualification tests in this repository.
