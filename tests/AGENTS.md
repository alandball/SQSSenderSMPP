# Test rules
- Test externally observable outcomes, particularly duplicate suppression, competing owners, crash recovery, partial multipart progress and queue deletion barriers.
- Give every network test a deadline and clean up only resources it created. Use synthetic IDs, message text and destinations.
- Fakes must preserve conditional-write semantics. Run AWS adapter tests against the local emulator and SMPP adapter tests against a TCP simulator.
- A regression test must fail for the original fault. Avoid assertions that simply duplicate implementation formulas.
- Do not silently skip unavailable integration dependencies; explain how to start them and fail the requested suite.
