# Security Policy

## Supported Versions

Workbench has implemented its application-foundation and data-and-identity phases but has not
published a production release.

Security reports concerning the current default branch and repository content are welcome. Once
Workbench begins publishing releases, this policy will identify which versions receive security
updates.

## Reporting a Vulnerability

Please report suspected vulnerabilities privately by emailing
[security@whitestagcollection.com](mailto:security@whitestagcollection.com). Do not open a public
issue or discussion for an undisclosed vulnerability.

Include as much of the following information as is practical:

- a description of the vulnerability and its potential impact;
- the affected component, version, commit, or URL;
- steps to reproduce or a proof of concept;
- any conditions required for exploitation; and
- suggested remediation, if available.

Please avoid including unrelated personal, confidential, or sensitive information.

We aim to acknowledge reports within three business days and provide an initial assessment within
ten business days. Investigation and remediation timelines will depend on the vulnerability's
complexity and severity, but we will provide updates while an accepted report remains unresolved.

If a report is accepted, we will work with the reporter on remediation and coordinated disclosure.
We will offer public credit unless the reporter prefers to remain anonymous. If a report is
declined, we will explain why when doing so would not create additional security risk.

## Security invariants

Security review should treat these as required controls:

- tenant authority comes only from a currently valid durable server-side session;
- tenant isolation is enforced independently by authorization, Entity Framework guards,
  tenant-consistent constraints, and SQL Server row-level security;
- identifiers, cookies, network location, application queries, and database connections do not
  independently grant tenant authority;
- state-changing browser requests require antiforgery validation;
- raw passwords, session tokens, recovery tokens, connection strings, migration credentials,
  backups, and data-protection secrets must not enter source control, logs, browser storage,
  container images, or audit metadata;
- web, operator, migrator, and setup/database-owner principals remain separate;
- public recovery and invitations remain disabled without provider-backed delivery and a shared
  multi-replica rate limiter; and
- a restored database cannot become ready until sessions, identity operations, and key material
  are invalidated and security versions are advanced.

Report cross-tenant access, authorization bypass, session or recovery replay, CSRF, SQL injection,
RLS bypass, secret disclosure, unsafe restore, or privilege escalation when reachable through a
supported deployment or realistic attacker boundary.

Ordinary behavior already authorized to a database owner or migrator is not itself a vulnerability,
but unintended exposure of those credentials or authority is reportable. Unsupported configurations,
developer-only behavior, and host compromise should be reported only when repository-controlled
defaults or deployment paths make them realistically reachable.

The repository threat model is documented in
`docs/security/data-identity-threat-model.md`.
