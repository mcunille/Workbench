# Server test guidance

For server tests, follow the testing boundaries and infrastructure isolation rules in [the server guidance](../src/Workbench.Server/AGENTS.md), along with [test method](README.md#test-method) for TDD, mutation testing, and Gherkin comments. These rules apply even when a change touches only this test directory.

Use [test ownership and cost](README.md) when choosing fixtures or deciding which layer should own
a new matrix of cases. Preserve distinct HTTP and SQL security boundaries when consolidating tests.
