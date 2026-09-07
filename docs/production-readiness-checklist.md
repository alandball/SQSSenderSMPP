# Production readiness checklist

No production launch or throughput commitment is approved until every mandatory item has an owner and evidence link.

## Carrier contract and protocol

- [ ] Aggregate TPS, per-bind TPS, maximum binds, permitted source IPs, and rollout/failure reserve are contractual.
- [ ] Outstanding request window and P95/P99 response latency are documented and tested.
- [ ] TRX versus TX/RX mode, credential sharing, bind-attempt throttles, enquire-link, inactivity, and response timers are documented.
- [ ] Primary/secondary endpoints and failover/failback ownership are tested from every AZ.
- [ ] TLS or approved encrypted private transport is in place.
- [ ] GSM-7/UCS-2/non-BMP, UDH/SAR/message-payload, TON/NPI, sender ID, validity, and registered-delivery rules pass certification.
- [ ] Retryable/permanent SMPP status mapping and `ESME_RTHROTTLED` recovery are provider-approved.
- [ ] DLR format, routing across binds, message-ID normalization, duplicate/out-of-order behavior, and retention are certified.
- [ ] The business has approved the `UNKNOWN` policy and carrier idempotency/query capability is documented.

## Correctness and data

- [ ] Producer contract requires stable `envelopeId` and `smsId`; payload-hash collisions/reuse are rejected.
- [ ] Schema versioning and backward/forward compatibility policy are agreed.
- [ ] DynamoDB conditional state transitions, TTL, PITR, encryption, capacity mode, alarms, and restore exercise pass.
- [ ] Partial envelope replay skips terminal children.
- [ ] Source envelope deletion never waits for DLR and occurs only after every child is durably terminal at submission stage.
- [ ] `UNKNOWN` is never silently converted to success, failure, or blind retry.
- [ ] DLQ retention exceeds source retention; first visible item alarms; replay performs ledger dry run and is audited.
- [ ] Reconciliation proves zero silent loss at burst and fault load.

## Capacity and resilience

- [ ] Message/encoding sample establishes the P95 and mean segments per logical SMS.
- [ ] 50,000 full envelopes drain in under 20 minutes with production telemetry enabled.
- [ ] 1.5x target one-hour qualification and 24-hour endurance pass.
- [ ] Replica count retains the approved target after one-AZ loss or the degraded objective is explicitly accepted.
- [ ] KEDA min/max, queue target, cooldown, and bind-rate controls are derived from measurements and carrier limits.
- [ ] Warm node/session capacity is ready before scheduled bursts.
- [ ] Hard kill, SIGTERM drain, node rotation, one-AZ loss, SMSC outage, and reconnect storm tests pass.
- [ ] Resource requests/limits come from profiles; all internal queues and outstanding operations are bounded.

## EKS, network, and security

- [ ] Private three-AZ EKS topology, managed system node group, and Karpenter application NodePool are operational.
- [ ] Zone/hostname topology constraints, PDB, PriorityClass, and rollout strategy are enforced.
- [ ] Per-AZ fixed egress or redundant private carrier path is allowlisted and failure-tested.
- [ ] VPC endpoints, security groups, and default-deny NetworkPolicies allow only required traffic.
- [ ] Dedicated EKS Pod Identity roles are least privilege; node credentials/IMDS are not exposed to pods.
- [ ] SMPP credentials and library license are runtime-mounted from Secrets Manager; rotation is tested.
- [ ] Containers are non-root, read-only, capability-dropped, seccomp-default, immutable by digest, SBOM'd, scanned, and signed.
- [ ] No message content, raw MSISDN, secrets, or unbounded IDs appear in logs, traces, metrics, crash dumps, or test reports.
- [ ] Threat model, privacy impact, retention, incident response, and penetration/security review are approved.

## Delivery and operations

- [ ] Terraform state is encrypted, versioned, locked, path-scoped, backed up, and recoverable; production plans require review.
- [ ] JFrog proxy/build-info/Xray/license/signature promotion gates pass.
- [ ] Helm values schema rejects unsafe/missing capacity, image, security, and secret settings.
- [ ] Argo GitOps promotion, self-heal policy, sync waves/window, rollback, and disaster recovery are rehearsed.
- [ ] Rancher registration, SSO/RBAC, audit, fleet policy, and break-glass access are tested without sharing ownership with Terraform/Argo.
- [ ] Dashboards expose envelopes, logical SMS, segments/PDUs, accepted rates, queue age, bind/window/TPS, throttle, unknown, DLR, and DLQ.
- [ ] Alerts and runbooks cover no binds, backlog age, throttling, unknown outcomes, DLQ, DynamoDB failure, carrier outage, credential expiry, and AZ loss.
- [ ] On-call, carrier escalation, RTO/RPO, maintenance windows, and load-event coordination are agreed.

## Runtime/library ADR

- [ ] Inetlab procurement, Linux/.NET 10 certification, support, security response, and container licensing are approved; or Java/jSMPP fallback is selected.
- [ ] Exact library version is pinned through JFrog and passes SAST/SCA/license policy.
- [ ] Adapter/conformance suite covers every provider-specific field and error behavior.
- [ ] No library vendor throughput claim is used as production evidence.

## Go/no-go record

Record the final:

- target logical SMS/s and PDU/s;
- average/P95 segments per SMS;
- per-bind safe TPS, window, and response latency;
- normal, one-AZ-loss, minimum, maximum, and surge bind counts;
- source/DLQ/ledger identifiers and retention;
- image digest, Helm chart version, GitOps revision, and Terraform state/version;
- approved `UNKNOWN` policy;
- test report and rollback exercise links;
- accountable architecture, engineering, security, operations, product, and carrier approvers.
