# Local validation — 0.1.0

Validated on Windows on 2026-09-07 with SDK 10.0.202, runtime 10.0.6, Inetlab.SMPP 2.9.39 and Moto 5.2.1.

On 2026-09-08, after resuming from the usage-limit pause, the unchanged application passed all 10 integration tests again. The standalone smoke passed after restarting the local services (50 acceptances, empty source queue, 5.641 seconds), and readiness was confirmed. The startup script now records the launched executable path explicitly because Windows can temporarily return a null process path immediately after launch.

| Check | Observed result |
| --- | --- |
| Locked NuGet restore | Passed |
| Release build | Passed; zero warnings/errors |
| `dotnet format --verify-no-changes --no-restore` | Passed |
| Unit/encoding/concurrency/lifecycle/configuration/pacing tests | 37 passed, zero skipped |
| Local AWS and TCP SMPP integration tests | 10 passed, zero skipped |
| Standalone worker smoke after a clean harness restart | 50 children accepted; 50 simulator acceptances; source queue empty; 5.766 seconds at configured local pacing |
| Release publish | Passed; output under ignored `artifacts/publish` |
| NuGet known-vulnerability query including transitive dependencies | No vulnerable packages reported by the configured NuGet source at validation time |

Integration evidence covers native conditional DynamoDB writes, SQS receipt visibility and deletion, 50-child processing and replay, explicit throttle retry, permanent failure, timeout, socket disconnect, out-of-order responses, early durable DLR and poison-envelope DLQ redrive. The fault harness is local and uses synthetic data only.

Raw reports are generated under ignored `artifacts/`. Reproduce with `build.ps1 test`, `integration` and `smoke`. The dependency query is not a substitute for work Xray, license review, container scanning or carrier certification. Docker image build, GitHub-hosted execution, real AWS/EKS, an independent SMPP simulator and production load/soak tests were not run here.
