# Enterprise SQS-to-SMPP sender

This repository contains the architecture baseline for a highly available SMS submission service that consumes Amazon SQS envelopes containing up to 50 logical SMS messages and submits them to an SMSC over SMPP 3.4 from Amazon EKS.

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

This is an architecture and delivery blueprint, not a deployable sender implementation. Final replica counts and the production go/no-go remain gated by the SMSC interface profile and the qualification tests in this repository.
