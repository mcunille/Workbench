# Server guidance

These rules supplement the root guidance, including TDD, mutation testing, and Gherkin comments.

- Unit-test business rules independently of HTTP and persistence. Test endpoint behavior through HTTP, using authenticated requests when applicable, so routing, binding, authorization, and response handling are exercised.
- When persistence is involved, use real SQL Server integration tests for transactions, constraints, concurrency, and tenant isolation. In-memory substitutes and mocked repositories do not establish database behavior. Include cross-tenant reads and writes and competing operations when those boundaries are affected.
- Use disposable test infrastructure and explicit test configuration. Prevent fallback to development or production connections, credentials, and inherited environment settings. Tests must not modify shared data; keep secrets out of logs and tracked files.
- Keep server-side business rules authoritative. Follow the accepted Workbench architecture and regenerate API declarations when the OpenAPI contract changes, as described in the root contribution guidance.
