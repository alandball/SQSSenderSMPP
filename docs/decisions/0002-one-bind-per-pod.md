# ADR-0002: One SMPP bind per pod

- Status: Proposed, pending carrier capacity confirmation
- Date: 2026-09-04

## Context

The worker may use one bind in each small pod or multiple binds in a larger pod. SMPP throughput comes from asynchronous outstanding operations and the carrier's TPS allocation, not from the number of binds inside a process or from larger CPU/memory alone.

## Decision

Run **one long-lived `bind_transceiver` per pod** in a Kubernetes Deployment.

Each pod is one:

- SMPP session and reconnect state machine;
- carrier TPS token bucket;
- outstanding-window semaphore;
- readiness and drain unit;
- replica/scaling unit;
- failure and observability cell.

Use a StatefulSet only if the carrier requires credential/identity per ordinal. Use separate transmitter and receiver deployments only if the carrier does not permit transceiver binds.

## Rationale

- Failure of a process or node removes one session rather than several.
- Replica count maps directly to carrier bind count and known capacity.
- KEDA and rollout limits can be clamped to the carrier's maximum sessions.
- Pod topology spread yields simple AZ capacity calculations.
- Each pod can stop intake, drain its window, and unbind independently.
- Smaller pods bin-pack well; a mostly I/O-bound session does not justify a large pod.
- Per-session metrics and throttling are unambiguous.

## Availability sizing

Provision the normal active bind count plus enough sessions to retain normal throughput after one of three AZs fails:

```text
active = ceil(targetPduRate / safePerBindPduRate)
provisioned = ceilToMultipleOf3(active * 3 / 2)
```

This is stricter than simple N+1. A single spare protects against one session failure; the `3/2` rule protects the target against loss of a whole equally loaded AZ. The carrier must contract the provisioned bind count and aggregate TPS, including any temporary rollout surge.

## Rollouts

- Preferred: provider allows one extra bind, `maxSurge: 1`, `maxUnavailable: 0`, startup jitter, readiness only after bind succeeds.
- When the bind cap has no spare slot: `maxSurge: 0`, `maxUnavailable: 1`; the service temporarily consumes its capacity reserve.
- A PDB with `maxUnavailable: 1` limits voluntary disruption. Topology spread applies across zone and hostname.
- Scale-down remains slow and drains before unbind. Scale-up has a capped rate so a backlog does not cause a login storm.

## Exceptions

Multiple binds per pod may be reconsidered only when all are documented:

- Carrier session rules make a one-bind pod topology impractical; or
- Measured pod/runtime overhead is a material cost or scheduling limit at the required bind count; and
- A multi-bind prototype passes the same one-AZ, rollout, hard-kill, and reconnect-storm tests; and
- Operators accept coarser scaling and a larger failure blast radius.

If approved, start with two binds per pod, keep independent state/limiters per bind, and do not share an unbounded work queue. Record the change in a replacement ADR.

## Consequences

- The replica floor can be tens of small pods when the carrier window is low. This is acceptable on EKS and preferable to hiding the same session count in fewer failure domains.
- Provider bind licensing/allowance directly caps `maxReplicaCount`.
- Planned campaigns should pre-scale because a new pod must be scheduled, retrieve secrets, connect, authenticate, and become bound before it adds capacity.
