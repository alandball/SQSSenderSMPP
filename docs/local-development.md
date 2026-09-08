# Local development

Prerequisites: .NET SDK 10.0.202 (latest patch in that feature band is allowed), PowerShell 7.4+, and Python 3.12+. Docker, AWS CLI, AWS credentials and work network access are not required. First setup downloads public NuGet/PyPI dependencies; subsequent builds/tests use local caches. Do not copy `.venv` or build outputs between PCs.

```powershell
# New checkout: point at your Python interpreter if it is not on PATH.
pwsh ./build.ps1 setup -PythonPath 'C:/path/to/python.exe'
pwsh ./build.ps1 test
pwsh ./build.ps1 integration
pwsh ./build.ps1 smoke
```

The current PC is already configured with an isolated `.venv`. All temporary runtime copies, process records and logs are under ignored `.local/`. Build outputs remain separate so a running simulator does not lock normal build files on Windows.

| Command | Result |
| --- | --- |
| `pwsh ./build.ps1 run` | Start/reuse emulator, simulator and sender in the background |
| `pwsh ./build.ps1 load -Envelopes 10 -Children 50` | Enqueue 500 synthetic local SMS; requires `run` first |
| `pwsh ./build.ps1 test` | Run unit, encoding, concurrency and lifecycle tests |
| `pwsh ./build.ps1 integration` | Start/reuse harness, run tests with isolated SQS queues and DynamoDB tables |
| `pwsh ./build.ps1 smoke` | Run the standalone sender and reconcile 50 ledger acceptances, 50 simulator acceptances and empty queue |
| `pwsh ./build.ps1 verify` | Check formatting and unit tests |
| `pwsh ./build.ps1 publish` | Publish the worker under `artifacts/publish` |
| `pwsh ./build.ps1 integration-down` | Stop only recorded local process trees; retain logs |

`run` reuses an already running copy. After code/config changes, stop the harness and run again to publish fresh runtime copies. `integration-down` is a **hard local stop**. It stops Moto too, so its in-memory queues and ledger disappear together. Use synthetic data only. Do not present Moto as a durable production datastore; the production adapter writes to DynamoDB.

For interactive graceful shutdown:

```powershell
pwsh ./build.ps1 integration-up
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src/Sender.Worker --no-restore
# Ctrl+C drains the worker. Do not also start a background worker on port 5080.
```

## Local endpoints

- AWS emulator: `http://127.0.0.1:5000`
- Sender: `http://127.0.0.1:5080/health/live` and `/health/ready`
- Simulator: `http://127.0.0.1:5081/stats`, SMPP TCP `127.0.0.1:2775`
- Source: `sender-local`; DLQ: `sender-local-dlq`; ledger: `sender-local`

The local credentials are literal dummy values `local`/`local`. The worker has no seeding, reset, or AWS resource-creation API. Only the loopback harness scripts create test resources.

Set a simulator fault using PowerShell:

```powershell
Invoke-RestMethod -Method Post http://127.0.0.1:5081/scenario/timeout
# Queue a synthetic test with the load command, inspect the ledger, then reset:
Invoke-RestMethod -Method Post http://127.0.0.1:5081/scenario/accept
```

Other scenarios: `permanent`, `throttle-once`, `disconnect`, `out-of-order`, `dlr`, `bind-reject`. Fault mode affects new requests. Bind rejection affects new binds, not a session already bound. Restart the simulator to reset counters and per-destination throttle history. Run smoke on an idle, empty local queue with scenario `accept`.

## Troubleshooting

- Restore errors: verify public NuGet/PyPI access, or use approved package mirrors on the work PC. Do not disable TLS validation. `dotnet restore --force-evaluate` is for intentional dependency changes; commit the updated lock files and then verify `--locked-mode`.
- Missing Python: `setup -PythonPath ...` creates `.venv`. The interpreter is a development prerequisite, not a runtime dependency of the sender.
- Busy ports: check existing local services; the harness uses 5000, 5080, 5081 and 2775. Do not stop unrelated processes.
- Checksum errors from the emulator: the pinned Moto 5.2.1 was tested with AWS SDK v4. Moto 5.1.12 produced incorrect checksums for empty SQS visibility responses. Do not disable AWS response-integrity checks to accommodate it.
- Check `.local/worker.out.log`, `.local/worker.err.log` and simulator/emulator logs. Readiness remains false during a bind failure or missing ledger. Startup with missing work configuration fails closed.
- Test reports are under `artifacts/tests` and `artifacts/integration`; smoke evidence is `artifacts/smoke.json`.
