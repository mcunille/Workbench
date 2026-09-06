# Finding bugs at verification boundaries

Use this guidance when a changed behavior crosses an execution boundary or its tests replace that boundary with a mock. Focus on assumptions introduced or affected by the diff; do not turn every review into a full-system audit.

## Choose checks that can contradict the implementation

Trace a representative real caller or documented invocation through the changed code to its observable result, including relevant unchanged producers and consumers. Identify the concrete assumption each boundary requires: accepted input, identity, serialization, ordering, transaction behavior, environment, or failure handling.

For the highest-impact plausible failures, state a trigger and predicted incorrect result before choosing a check. Prefer the smallest experiment that distinguishes correct behavior from that failure. Inspect existing tests for whether their assertions would detect it; test counts, coverage percentages, and a passing build do not answer that question. Investigate failure, retry, or concurrent execution when the changed contract depends on them, rather than applying an exhaustive scenario list to every patch.

## Inspect the evidence boundary

Read the relevant test setup and doubles, not only test names and results. Establish what actually executes and where a fixture supplies success. A fake command runner can verify argument construction and sequencing while accepting invalid commands; an in-memory store can bypass database constraints, locking, and transaction isolation. Use the real receiver for a focused check when feasible within review permissions.

Match evidence to the reviewed source and supported invocation. Check material differences between a manual drill, CI, and the shipped path: checkout transformations, operating system, generated artifacts, runtime identity, configuration, and dependency versions. Investigate differences that can affect the changed behavior; do not require every possible environment combination.

## Select a relevant boundary probe

| Changed boundary | Useful focused check |
| --- | --- |
| PowerShell or another host emits shell commands | Extract the actual generated payload and syntax-check it with the receiving shell. Exercise supported checkout line endings and relevant quoting or argument preservation. Parsing the host script or using a different shell does not validate the payload. |
| Generated configuration, queries, or API payloads | Feed the produced value to the actual parser or consumer; include a boundary input that challenges the changed contract. Substring assertions alone can accept invalid output. |
| Persistence, authorization, or state transitions | Exercise the affected constraint or transition with the relevant engine and identity. Where correctness relies on concurrency or retries, use competing operations or repeated delivery and assert the invariant. |
| Installer, deployment, or process lifecycle | Follow the documented entry point and inspect partial-failure handling. Prefer a disposable targeted probe before a full installation; identify what it leaves untested. |

For example, a Windows PowerShell here-string passed to Linux Bash can retain CRLF and break shell control flow even though PowerShell parsing and simulated Docker tests pass. A syntax-only check of that exact CRLF-derived payload in Linux Bash can detect the defect without provisioning services. Passing that check establishes syntax, not successful installation.

## Preserve review scope and report evidence precisely

Keep probes non-destructive and within the skill's read-only contract. Use disposable scratch artifacts where needed; do not modify tracked files, retained installations, or production systems to obtain evidence. When a check is unavailable, continue static tracing and other permitted checks, then name the specific unverified scenario.

Before choosing the verdict, reconcile each material failure hypothesis with evidence: disproved by a relevant check, substantiated as a finding, or still unverified. Report findings with a concrete trigger, code path, and observable impact; do not promote a hypothetical defect to a finding merely because a runtime was unavailable. A demonstrable test gap can itself be a finding when tied to a specific behavior or repository verification requirement. Preserve the skill's existing verdict rules and disclose remaining coverage limits without presenting mocked or unrelated execution as end-to-end proof.
