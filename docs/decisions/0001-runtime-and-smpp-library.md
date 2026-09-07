# ADR-0001: Runtime and SMPP library

- Status: Proposed, pending qualification spike and procurement
- Date: 2026-09-04

## Context

The service is greenfield and the available engineering organization can operate .NET, Java, or Go. Runtime throughput is not expected to be the primary constraint; carrier TPS, response latency, permitted SMPP window, library protocol behavior, and recovery semantics dominate. The enterprise needs a supportable SMPP 3.4 implementation with asynchronous request correlation, multipart handling, DLRs, TLS, reconnect control, diagnostics, and long-duration stability.

## Decision

Use **.NET 10 LTS Worker Service** and qualify **Inetlab.SMPP** as the primary client/server library. Put every library-specific type behind an internal `ISmppSession` adapter so the worker, ledger, error policy, and tests do not depend directly on the vendor API.

.NET 10 is an LTS release supported through November 2028. [Microsoft .NET support policy](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support)

Inetlab.SMPP is current and commercially supported, with a source-license option. Its package is compatible with modern .NET targets, but computed framework compatibility is not the same as explicit vendor certification. Confirm .NET 10/container support, production licensing, security support, and the exact supported API before acceptance. [Inetlab product and licensing](https://inetlab.com/Products/Inetlab.SMPP.html) and [NuGet package](https://www.nuget.org/packages/Inetlab.SMPP/)

The worker uses:

- `BackgroundService`/generic host lifecycle;
- AWS SDK for .NET with low-level SQS and DynamoDB clients;
- bounded `Channel<T>` for admission and fair scheduling;
- concurrency and token-bucket limiters for window/TPS control;
- OpenTelemetry logs, metrics, and traces;
- source-generated JSON serialization and schema/semantic validation.

## Why

- A supported commercial protocol component reduces delivery risk for carrier quirks, encoding, TLVs, reconnects, and DLR handling.
- .NET has strong asynchronous I/O, bounded channels, diagnostics, and EKS/AWS support.
- One small process per bind avoids any requirement for a large managed-runtime footprint.
- The internal adapter preserves leverage and enables conformance testing against more than one implementation.

Vendor performance claims are evidence for a spike, not sizing input. The service must meet the test matrix in `docs/testing.md` with production telemetry enabled.

## Alternatives

### Java 25/21 LTS with jSMPP

This is the preferred open-source fallback. jSMPP 3.0.2 is current in 2026, Apache-2.0 licensed, supplies client/server functionality and examples, and supports concurrent request correlation. Use a thin worker with AWS SDK v2 and explicit bounded execution; do not hide critical admission/visibility behavior behind a general integration framework. [jSMPP repository](https://github.com/opentelecoms-org/jsmpp)

Cloudhopper has an excellent historical asynchronous/window model but its common stable lineage is old and includes legacy Netty dependencies. It should not be introduced unmodified without a security and maintenance plan.

Consequences: no commercial license, a strong telecom ecosystem, and higher internal responsibility for the SMPP library/support posture. Java becomes the selected runtime if Inetlab commercial/source licensing or .NET 10 certification is unacceptable and jSMPP passes the same qualification suite.

### Go with `fiorix/go-smpp/v2`

Go offers excellent pod density, startup, and straightforward bounded concurrency. `go-smpp` v2.1.0 was revived in 2026 and includes windowing, reconnects, TLS, codecs, DLR parsing, and a test server. Its own documentation says it is not fully compliant, and source review identifies behaviors that require an adapter or maintained fork: immediate over-window errors, synchronous submit calls, a latency-sensitive DLR handler, common-path rate limiting, and reconnect jitter. [Go SMPP repository](https://github.com/fiorix/go-smpp) and [releases](https://github.com/fiorix/go-smpp/releases)

Consequences: smallest footprint, but materially greater ownership of protocol/session correctness. Select only if the organization accepts maintaining the adapter/fork and the full qualification suite passes.

### Custom SMPP implementation

Rejected. Reimplementing binary framing, asynchronous correlation, encodings, segmentation, TLVs, DLR variations, timers, and recovery creates avoidable correctness and operational risk.

## Qualification gates

The ADR becomes Accepted only when all are true:

- Procurement and legal approve the exact production/container/source license and redistribution model.
- Vendor confirms .NET 10 Linux container support and a security/update support path.
- Actual carrier bind, window, TLS, TON/NPI, encoding, TLV, DLR, and message-ID profiles pass conformance tests.
- The exact library build sustains 1.5x target for one hour and target for 24 hours without unbounded memory, request leakage, or reconnect instability.
- Failure injection proves response timeout, out-of-order response, throttle, socket loss, bind rejection, DLR duplication, and graceful drain semantics.
- JFrog Xray and license policy pass; the image and SBOM are signed and promoted immutably.

If a gate fails, execute the same matrix against Java/jSMPP before considering custom fixes or Go.

## Consequences

- The enterprise accepts a commercial dependency and must budget renewal/support.
- The library license file is injected from Secrets Manager at runtime and never stored in Git, JFrog layers, Helm values, or Terraform state.
- Application contracts and observability stay independent of library-specific response classes.
- A two-week protocol/performance spike precedes feature development; it is not deferred until production certification.
