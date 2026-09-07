# Capacity model and service objectives

## 1. Unit of capacity

Keep four different units visible in every dashboard and test report:

1. **SQS envelopes**: source records, each carrying 1-50 logical SMS.
2. **Logical SMS**: one business message to one destination.
3. **SMPP PDUs**: one `submit_sm` per encoded segment.
4. **Accepted submissions**: PDUs acknowledged with a successful `submit_sm_resp`.

SQS messages/s is not an adequate throughput measure. Long GSM-7 messages, extension characters, UCS-2, UDH, and provider rules can turn one logical SMS into multiple charged/submitted PDUs.

## 2. Workload calculation

Given the stated burst:

```text
envelopes              = 50,000
logical SMS/envelope   = 50
duration               = 20 minutes = 1,200 seconds

logical SMS            = 2,500,000
average logical rate   = 2,500,000 / 1,200 = 2,083.33 SMS/s
```

An exact 2,083 SMS/s capacity would consume the whole 20-minute objective with no allowance for segmentation, retries, throttling, pod loss, or arrival skew. Use:

```text
targetLogicalRate = averageLogicalRate * trafficAndFailureHeadroom
targetPduRate     = targetLogicalRate * measuredAverageSegmentsPerSms
```

Recommended initial qualification points:

| Stage | Logical SMS/s | Mean segments | Required PDU/s |
| --- | ---: | ---: | ---: |
| Minimum mathematical average | 2,083 | 1.0 | 2,083 |
| Initial operating target | 3,000 | 1.0 | 3,000 |
| Initial segmented planning case | 3,000 | 1.2 | 3,600 |
| Stress point | 4,000 | 1.2 | 4,800 |

The message mix must be measured from representative, de-identified payloads. Do not assume one segment per message.

## 3. Per-bind capacity

For one SMPP session:

```text
latencyCeilingPduPerSec = approvedOutstandingWindow / submitResponseP95Seconds
safePerBindPduPerSec    = min(carrierPerBindTps, latencyCeilingPduPerSec) * utilizationFactor
activeBinds             = ceil(targetPduRate / safePerBindPduPerSec)
threeAzProvisionedBinds = ceilToMultipleOf3(activeBinds * 3 / 2)
```

Use a utilization factor between 0.70 and 0.85 until soak tests demonstrate stable behavior. The `3/2` factor ensures that the remaining two of three equally populated AZs retain the required active session count after one AZ is lost.

The SMPP 3.4 specification leaves the maximum outstanding operations to the SMSC and gives a guideline of no more than ten. Any larger window must be explicitly approved and proven with the carrier. [SMPP 3.4 specification](https://smpp.org/SMPP_v3_4_Issue1_2.pdf)

### Illustrative scenarios

These are examples, not final sizing:

| Carrier/session inputs | Safe capacity | Active binds for 3,600 PDU/s | Provisioned for one-AZ loss |
| --- | ---: | ---: | ---: |
| 100 TPS cap, window 10, P95 100 ms, 80% | 80 PDU/s | 45 | 69 (23/AZ) |
| 250 TPS cap, window 25, P95 100 ms, 80% | 200 PDU/s | 18 | 27 (9/AZ) |
| 500 TPS cap, window 50, P95 100 ms, 80% | 400 PDU/s | 9 | 15 (5/AZ) |

If the carrier's aggregate account TPS is below the target PDU rate, adding pods cannot meet the requirement. The carrier contract must include both normal capacity and enough bind/TPS allowance for rollout and failure reserve.

## 4. Queue scaling target

KEDA's SQS `queueLength` is an envelope target per pod. Derive it from measured service rate:

```text
envelopesPerSecondPerPod = safePerBindPduPerSec / (50 * averageSegmentsPerSms)
queueLengthTarget        = floor(envelopesPerSecondPerPod * desiredQueueDelaySeconds)
```

Example: at 200 PDU/s per bind, 50 children/envelope, 1.2 segments/SMS, and a 10-second target queue delay:

```text
200 / (50 * 1.2) = 3.33 envelopes/s/pod
queueLength target = floor(3.33 * 10) = 33 envelopes/pod
```

This value must be calibrated because partially complete envelopes, throttling, item validation, and SQS approximate metrics affect the relationship. Keep prefetch bounded so `ApproximateNumberOfMessagesNotVisible` represents real processing instead of hidden local backlog. KEDA includes in-flight messages by default. [KEDA SQS scaler](https://keda.sh/docs/2.20/scalers/aws-sqs/)

Recommended starting policy:

- `minReplicaCount`: at least 3 during quiet periods; set to the full forecast daytime requirement before campaigns.
- `maxReplicaCount`: the smaller of the carrier bind allowance, aggregate TPS allocation, and tested EKS capacity.
- Scale-up stabilization: 0 seconds, with a capped step and randomized bind startup.
- Scale-down stabilization/cooldown: 600 seconds.
- Secondary signal: oldest source-message age or admitted PDU backlog.
- Schedule: raise the minimum 10-15 minutes before known bursts so nodes, images, secrets, and binds are warm.

## 5. Resource starting point

For a .NET one-bind pod, begin non-production profiling at:

```text
requests: 500m CPU, 512 MiB memory
limits:   1 CPU, 1 GiB memory
```

These are test inputs, not production recommendations. Final requests/limits come from the 1-hour peak soak and 24-hour endurance test with production logging and OpenTelemetry enabled. Larger pods do not raise the carrier window or TPS limit.

## 6. SLO and acceptance proposal

| Objective | Proposed target | Measurement |
| --- | --- | --- |
| Burst drain | 50,000 full envelopes in <20 minutes | Source oldest age and final envelope completion timestamp |
| Sustained submission | >= calculated segmented PDU target | Successful `submit_sm_resp`/s over 15-minute windows |
| Silent loss | 0 | Input-child count reconciles to accepted + permanent rejected + unknown |
| Ambiguous outcome | 100% surfaced; no blind retry by default | Ledger `UNKNOWN` records and alerts |
| DLQ | 0 under nominal load; alert on first item | SQS DLQ visible count |
| One-AZ fault | Target rate retained or documented degraded target | Chaos test with one-third of pods/nodes removed |
| Memory/backlog bound | No monotonic growth; channels remain within configured capacity | Runtime and application metrics |
| Shutdown | No new receive after readiness false; terminal work persisted | SIGTERM test and ledger/SQS reconciliation |

Availability objectives exclude a carrier outage only if the external-dependency exclusion is written into the service definition. The dashboard should show both platform availability and end-to-end carrier availability so exclusions are visible rather than hidden.

## 7. Required performance report

Every release candidate records:

- Runtime, exact SMPP library/version/license, image digest, Helm chart version, and node instance type.
- Carrier/harness TPS/window/latency/throttle configuration.
- Message encoding distribution and measured segment multiplier.
- Accepted, rejected, throttled, timed-out, and unknown PDU rates.
- Queue drain curve and oldest-message age.
- Per-pod window utilization, channel depth, CPU, memory, GC, socket, and reconnect results.
- Duplicate suppression and ambiguous-outcome counts.
- Results at 1.0x, 1.5x, and stress load, plus the one-AZ-loss run.

Capacity is approved from this report, not from a library vendor's loopback benchmark.
