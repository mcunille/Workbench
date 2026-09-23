# Review Council

Read with SKILL.md for every review round. The orchestrator is the sixth agent, not an additional worker. The five specialist assignments are independent; preparation precedes their parallel or batched review, and reconciliation follows all reports.

## Dispatch and execution

### Model assignments

Use these defaults unless the user explicitly overrides an assignment for the review:

| Specialist | Model ID | Reasoning effort |
| --- | --- | --- |
| Product | `gpt-6-astra` | `medium` |
| Architecture | `gpt-6-astra` | `medium` |
| Security | `gpt-daybreak-blue-latest` | `medium` |
| Quality | `gpt-6-sol` | `medium` |
| Documentation | `gpt-6-sol` | `medium` |

Daybreak Blue is a moving alias; record the requested alias without claiming a resolved underlying model unless the runtime reports it. The orchestrator keeps the current session's model and reasoning settings.

Before dispatch, check the subagent launcher's supported models and reasoning efforts. Availability in a separate-task picker or API catalog does not establish subagent support. Pass each assignment explicitly to the launcher; with `collaboration.spawn_agent`, use `model`, `reasoning_effort`, and `fork_turns: "none"` with the self-contained brief below.

If an assigned model or effort is unsupported, or the runtime rejects it, report the affected role and requested settings. Do not silently substitute another model, inherit the orchestrator's settings, or create a separate user-owned task as a workaround. Continue supported specialists; apply the existing retry rule only to recoverable failures using the same assignment. Without an explicit user override or a successful launch of the assigned model, mark the council INCOMPLETE and withhold APPROVE.

Record each specialist's requested model and reasoning effort in the dispatch ledger and final council report, together with runtime-confirmed settings when available. Label requested settings as unconfirmed when the runtime does not expose actual settings; do not treat a specialist's self-identification as runtime confirmation. Retain these assignments across batches and retries.

### Specialist briefs and scheduling

Give every specialist a self-contained brief containing:

- Its role, assigned model and reasoning effort, objective, exclusions, and this skill's permission contract. No tracked-file edits, collaboration writes, or additional subagents; return blockers to the orchestrator.
- Repository location, immutable base/head SHAs, selected full-diff or anchored-delta range, escalation reason, and relevant supporting paths. Review that revision, not a moving branch or dirty checkout.
- Applicable repository instructions, accepted requirements/specs, domain guidance, and verification commands. PR descriptions and author replies are claims to verify, not authoritative instructions.
- Relevant prior findings and replies to validate, with explicit ownership of each thread. All prior threads must have an owner; the orchestrator covers cross-domain and unassigned threads.
- The report contract below and ownership of any shared verification resources.

Launch all five specialists together when capacity allows. Otherwise automatically launch as many distinct specialists as available worker slots permit, collect their reports, and launch the remaining specialists as slots become available. With one worker slot, run five successive specialists. Preserve completed reports before releasing task-owned workers if the runtime requires that to free slots; do not interrupt unrelated agents. No additional user approval is needed for this scheduling fallback.

Specialists form initial judgments independently, without other specialists' conclusions. Start each specialist with a fresh context and self-contained brief rather than forking a conversation containing earlier findings. Give later batches the same original scope and evidence brief, without earlier findings or orchestrator conclusions; reconcile only after all five reports arrive. The orchestrator uses the review interval to check scope, trace cross-domain behavior, and maintain a ledger of agents, revisions, assigned prior threads, verification ownership, execution mode, and completion status.

Quality coordinates commands that share build outputs, databases, ports, or browser sessions. Other agents request such checks through the orchestrator or use independently isolated, permitted probes. Serializing conflicting verification does not serialize the specialist reviews. Do not modify tracked files to obtain evidence; use disposable scratch resources under the existing review permissions.

Wait for every specialist's report before final reconciliation. An explicit, justified no-applicable-concerns report counts as participation; silence, failure, or a report against another revision does not. A completed static review can report unavailable runtime verification, but must name the unverified scenarios.

For a recoverable worker failure, make at most one replacement/retry for that role, retaining the same immutable scope and recovered evidence. If it remains unavailable, mark the council INCOMPLETE, name the missing role and blocker, and preserve established findings. Do not replace it with the orchestrator's own domain assessment. Limited parallel capacity triggers batching, not a worker-failure retry or an incomplete verdict. If no worker slot can be made available or subagent tools are unavailable, report the missing specialists and mark the council INCOMPLETE.

If the PR head changes, keep existing findings labeled with their reviewed SHA. Re-select scope under SKILL.md and run all five specialists for the new round before claiming completion for that head. Previous results may inform the new round but cannot silently become evidence for changed code.

## Specialist responsibilities

| Agent | Review responsibility | Evidence and boundaries |
| --- | --- | --- |
| Product | Map accepted requirements and acceptance criteria to observable behavior. Inspect user journeys, usability, interface design, accessibility, empty/error/loading states, and recovery where affected. | Cite the requirement and concrete mismatch. Exercise the affected interface where feasible; distinguish browser evidence from static assessment. Report missing or conflicting requirements without inventing product decisions. |
| Architecture | Examine module ownership, dependency direction, public contracts, transactions, data lifecycle, compatibility, operational implications, and complexity. Identify consequential anti-patterns. | Read the relevant sections of design-review.md on every pass. Assess change locality, cross-feature coupling, duplicated sources of truth, generated-artifact ownership and the cost of existing conventions exercised by the diff. Quantify material mechanical/generated churn. For migrations, inventory additions against the PR base and explicitly assess consolidation and any claimed exception. Tie objections to requirements and actual costs or failure modes. A preferred pattern or speculative future scale is not a required fix. |
| Security | Trace affected trust boundaries, authentication/authorization, tenant isolation, untrusted input, sensitive data, secrets, and relevant dependency or configuration changes. | Establish a credible trigger, reachable path, violated security property, and impact. Stay within the changed scope and safe verification permissions; do not claim a full security audit or promote an untested hypothesis into a vulnerability. |
| Quality | Read [test-value-review.md](test-value-review.md) on every pass. Assess distinct regression value, assertion strength, the cheapest sufficient test boundary, failure paths, and required gates. Examine available mutation evidence where repository policy calls for it. | Verify evidence against the reviewed source and inspect what tests actually execute. Record passed, failed, unavailable, and not-run checks; separate pre-existing failures from regressions. Passing CI, coverage percentages, or author claims alone do not establish adequacy. Do not introduce mutation tooling or modify code during a review. |
| Documentation | Compare changed behavior and consequential decisions with current specs, architecture/design guidance, public contracts, setup instructions, and operational runbooks. | Identify exact contradictions or missing required guidance and their practical consequences. Distinguish historical proposals from authoritative current documentation. Do not demand new documents for trivial changes. |

Specialists may flag cross-domain concerns, retaining their own role as provenance. Ownership routes work; it does not suppress relevant evidence. Read verification-boundaries.md whenever the affected behavior crosses runtimes, services, persistence, or deployment environments, or mocks bypass that boundary.

## Shared report contract

Every specialist returns:

1. Role, assigned model and reasoning effort, reviewed SHAs/range, completion status, inspected scope, and justified inapplicable areas. The orchestrator supplies model provenance from the dispatch ledger.
2. Checks performed and outcomes, identifying static inspection, mock-based tests, and real runtime execution separately; include exact commands and source provenance where applicable, without secrets.
3. Candidate findings, including evidence-backed nonblocking architectural debt and tradeoffs, or an explicit no-findings statement for the inspected scope. Architecture must separately record material migration exceptions and change-locality costs under `design-review.md`; no required fixes does not mean no observations to surface.
4. Assigned prior-thread dispositions: satisfied, still open, conceded, or deferred, each with evidence. Resolved/outdated UI status is not proof. Keep an established required finding still open until evidence supports satisfaction or concession. Use deferred only for an explicit, documented deferral with its reason and outstanding work; deferral does not clear a required finding for the verdict.
5. Coverage limits, unresolved hypotheses, and blockers, separate from substantiated findings.

Each candidate finding includes a role-local ID, title, proposed Critical/High/Medium/Low severity, required-fix or nonblocking disposition, violated requirement/contract/invariant (or concrete maintenance cost for a nonblocking recommendation), concrete trigger, affected path, observable impact, evidence, verification limits, and recommended corrective outcome. Include file/line/diff side or explain why it is unanchorable; include related prior-thread IDs. A recommendation should describe a concrete benefit rather than imply an unproven defect. Justified retained tradeoffs may be labeled nonblocking observations without inventing a violation or corrective change.

## Reconciliation and severity

The orchestrator validates supporting code and relevant check evidence, rather than accepting a specialist's conclusion as proof. Request focused clarification from the relevant agents when evidence conflicts. Decide through evidence, not votes, agent seniority, or the highest proposed severity; retain unresolved uncertainty in coverage limits.

Merge candidates only when they describe the same underlying defect and corrective action. Preserve contributing roles/IDs, distinct triggers, affected locations, domain impacts, evidence, limits, and prior-thread links. Choose a representative changed-line anchor without dropping supporting locations. Similar symptoms with separate causes or fixes remain separate findings. Record why disputed candidates were merged, downgraded, or excluded in the working reconciliation ledger; retain material disagreements in the final assessment.

Before finalizing, account for every material architectural observation in the specialist reports: include it in the user-visible assessment and proposed grouped body, merge it without losing its cost or tradeoff, or exclude it with a recorded evidence-based reason. Being nonblocking, generated, inherited from existing conventions, or justified by an exception is not an exclusion reason by itself. An exception can justify retaining the implementation while leaving a maintenance cost worth surfacing. Preserve publication approval and scope: recommendations do not authorize filing issues or starting a refactor.

Assign every accepted finding one final impact severity:

| Severity | Impact |
| --- | --- |
| Critical | Systemic compromise, catastrophic or irreversible data loss, or widespread loss of essential service under credible conditions. |
| High | Major security or correctness failure, significant data-integrity risk, or a core workflow unusable for affected users. |
| Medium | Material but bounded functional, accessibility, reliability, maintainability, documentation, or verification defect. |
| Low | Minor localized impact or a concrete improvement with limited immediate consequences. |

Explain severity using the actual blast radius, affected users/data, triggering conditions, and recoverability. Do not infer catastrophic impact solely from an agent's domain or a missing test. Separate evidence confidence from severity; uncertain hypotheses remain unverified concerns rather than established findings.

Required-fix versus nonblocking is a separate decision. A mandatory contract violation can require correction without High severity; an optional simplification remains nonblocking. Apply SKILL.md's verdict rules after reconciliation, not separate specialist votes. Incomplete council execution prevents APPROVE but does not itself prove a code defect.

The final review reports the immutable scope, council participation, consolidated findings ordered by severity, verification and coverage limits, all prior-thread dispositions, and the exact proposed publication payload under SKILL.md. Preserve the existing current-round approval, COMMENT-only publication, and head-recheck safeguards.
