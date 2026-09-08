# Implementation status: 0.1.0

This is the local implementation of the `Architect EKS SMPP SMS platform` task and its later DynamoDB/idempotency discussion. The architecture documents remain the target; this document describes the code actually delivered. The vendor SMPP version is still unconfirmed. No work infrastructure was contacted or changed.

## Projects

| Project | Responsibility |
| --- | --- |
| Sender.Core | Versioned envelope model, validation, ownership/state transitions, bounded scheduling, submission pacing, metrics and lifecycle |
| Sender.Aws | Low-level AWS SDK v4 SQS and conditional DynamoDB adapters; separate delivery state store |
| Sender.Smpp | Inetlab.SMPP 2.9.39 adapter; one transceiver, encoding/segmentation, configurable status policy and durable DLR acknowledgement |
| Sender.Worker | Generic-host background worker with ASP.NET health endpoints and optional OTLP metrics/traces |
| Sender.Harness | Loopback-only programmable Inetlab TCP server and HTTP controls |
| Sender.Tests | Unit, concurrency, lifecycle, encoding and local AWS/SMPP integration tests |

There is no AI/LLM dependency in the runtime. AGENTS.md and scoped rules guide AI-assisted development.

## Actual submission semantics

1. Reserve one of `MaxActiveEnvelopes` slots before one SQS receive. Long-poll for 20 seconds. Each slot holds at most 50 children; a bounded channel schedules envelopes round robin at logical-SMS boundaries. A fixed `Window` worker count bounds outstanding PDUs. A multipart child submits its segments sequentially.
2. Parse the v1.0 contract, rejecting missing/unknown/duplicate properties, invalid IDs, invalid E.164 numbers and invalid envelope sizes. Structural violations retain the whole envelope for native DLQ redrive. Validly structured business errors (expired SMS, unsupported Unicode/profile/source) become durable child rejections.
3. Read the child ledger with strong consistency. Keys are `tenantId/smsId` (`default` for absent tenant). A stable canonical payload hash detects conflicting reuse. Terminal entries suppress replay; a hash conflict retains the envelope.
4. Acquire `CLAIMED` with insert-only or revision compare-and-swap. A different owner cannot overwrite a newer revision. The owner holds a bounded lease. There is no Redis/cache correctness path.
5. For each pending segment, wait for its submission slot in the paced bucket, confirm the session and validity, then conditionally persist `SEND_STARTED` **before** calling Inetlab. This deliberately favors duplicate prevention over blind recovery of an uncertain side effect.
6. Persist the response using a bounded token independent of shutdown cancellation. Accepted segments survive redelivery. Explicit configured retry statuses become `RETRYABLE` with a jittered not-before time. A multiplicative rate reduction and one-second admission pause apply on retryable results; successes gradually restore configured TPS.
7. Retry budget is per logical SMS: successful preceding segments do not consume the failed-attempt budget. On exhaustion, `RETRY_EXHAUSTED` stops automatic sends and retains the envelope for DLQ redrive. Replay of an exhausted child requires an audited conditional policy change; simply moving its envelope out of the DLQ does not reset it.
8. Timeout, cancellation after possible transmission, disconnection, unmapped status and missing success message ID become `UNKNOWN`. An expired `SEND_STARTED` also becomes `UNKNOWN`. No code automatically retries it. If a multipart child becomes unknown or permanently rejected, later pending segments remain unsent, with the parent disposition explaining why.
9. Delete an envelope only when all children are accepted, permanently rejected or durably unknown/quarantined. Unknowns are reconciled from the ledger; they are not routed to the source DLQ. Deletion does not wait for handset delivery.

Code/JSON state names use PascalCase (`SendStarted`, `RetryExhausted`, etc.). The uppercase names above match the architecture terminology. Ledger fields `pk`, `revision`, `state`, `expiresAt` are queryable attributes; `data` contains the versioned .NET ledger representation, including per-segment status and SMSC IDs. Neither SMS text nor phone numbers are stored there.

An AWS write timeout is not interpreted as a miss. If a claim or result write committed but its response was lost, the envelope remains and the next strongly consistent read determines recovery. Revision checks fence stale writers. TTL is cleanup only: records are never automatically reclaimed because their TTL timestamp passed. Configure retention longer than **every permitted manual replay window**, not just ordinary SQS retention.

## SMPP and delivery receipts

- One `SmppClient` per worker process; application-managed reconnect with exponential jitter. Automatic library reconnect/repeat-submit is disabled. No custom binary SMPP codec.
- Provider settings include version byte, TLS, source/destination TON/NPI, response and enquire-link timers, UDH8/UDH16/SAR concatenation, maximum segments and explicit retry/permanent statuses. Initial 0x34 is a local test assumption, not vendor certification.
- Library segmentation handles GSM basic/extension characters and UCS-2. Non-BMP characters (including emoji) and embedded NUL are explicitly rejected. Concatenation reference derives from the stable SMS ID. A stored encoding-profile hash prevents multipart replay across an incompatible profile/library change.
- Submission uses a single PDU and its correlated response per call. The application pacing gate does not throttle enquire-link or DLR responses. Sender window, TPS and total replica count still need carrier-approved settings.
- DLR callbacks enqueue into a bounded 256-item inbox. Acknowledgement waits for a successful DynamoDB write; a full inbox returns queue-full. The separate item key is `dlr/providerProfile/SHA256(normalizedMessageId)`. Message IDs are currently normalized by trimming only. The first terminal receipt wins, so late intermediate receipts do not regress final state. Lookup joins from each accepted segment's provider message ID; DLRs may arrive before acceptance is recorded.
- Non-DLR mobile-originated traffic is explicitly rejected. Failed receipt persistence is not acknowledged successfully, so carrier redelivery behavior is a certification dependency.
- TLS uses platform trust and rejects certificate policy errors. Work mode requires a valid injected Inetlab license. Local development uses the package's evaluation behavior; production licensing and throughput qualification remain external gates.

## Lifecycle and observability

`/health/ready` requires a bound session, successful ledger probe, admission capacity, no throttle pause and no drain. `/health/live` only checks the local HTTP process. Shutdown stops receives, allows admitted work/heartbeats to drain, then unbinds. At the shutdown deadline, cancellation makes possible sends unknown and leaves unfinished receipts undeleted. Visibility failure cancels that receipt's work; no new children are sent on lost visibility ownership.

The `Sender` meter exports bounded outcome/error counters, dedupe/deletion counters, active envelopes and submit duration. `Sender` traces sample 1% by default. Enable OTLP with `Telemetry__EnableOtlp=true` and standard `OTEL_EXPORTER_OTLP_ENDPOINT` configuration. JSON console logs omit message bodies, phone numbers and business IDs; vendor PDU logging/tracing is disabled. This release has no Prometheus scrape endpoint or complete production dashboard/alert package.

## Evidence and remaining qualification

See `local-development.md` for executable commands and `testing.md` for the full target matrix. Local tests cover concurrent claims, stale writers, payload conflicts, missing acceptance writes, partial multipart retry, timeout/disconnect ambiguity, throttle response, out-of-order response correlation, early DLR persistence, visibility, graceful drain and 50-child SQS deletion/replay.

Pending work-environment qualification includes:

- Actual SMSC version, TON/NPI, status table, DLR text/TLV variants and ID normalization; library procurement/license and .NET 10 Linux certification.
- An independent simulator and Toxiproxy, real process hard-kill/restart orchestration, TLS/secret-rotation and full malformed-PDU testing. Current client/server protocol tests share Inetlab.
- Provider primary/secondary failover routing, enhanced reconciliation tooling, complete operational metrics/dashboards and production security scans.
- AWS IAM/Pod Identity, DynamoDB capacity/TTL/PITR, native service edge cases, EKS drain timing and carrier session/bind limits.
- Multi-AZ, 50k-envelope burst, 1.5x load and 24-hour endurance tests. Laptop smoke timing is not a throughput commitment.
- Existing work Helm/Argo/JFrog/GitHub build-deploy integration. The Dockerfile is supplied for that pipeline but cannot be image-built here without Docker.

This release is a tested local foundation with real adapters, not a production go-live approval.
