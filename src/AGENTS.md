# .NET rules
- Target the pinned .NET 10 SDK; use nullable reference types, implicit usings, file-scoped namespaces, async I/O and dependency injection.
- Keep domain and orchestration independent of AWS/Inetlab types. Add abstractions at side-effect boundaries, not for every class.
- Propagate cancellation. Once an external send may have started, preserve ambiguity and use a bounded independent token for recording its result.
- Use bounded admission and concurrency. Avoid async void, fire-and-forget I/O, sync-over-async, automatic submit replay and unbounded Task.WhenAll.
- Validate options at startup; production must fail closed on absent configuration. Never substitute a simulator when a dependency fails.
- Use source-generated JSON for contracts and persisted models. Reject unknown contract fields and conflicting IDs. Persist no SMS text or raw phone numbers in the ledger.
- Use structured, redacted logs and bounded metric dimensions. Do not log SDK exceptions that may contain request data or credentials.
- Use compare-and-swap writes for every ledger transition. Never use cache hits/misses or DynamoDB BatchWriteItem as ownership authority.
- Prefer clear state transitions and executable tests over clever general frameworks. Use .editorconfig and compiler/analyzer checks for formatting and mechanical rules.
