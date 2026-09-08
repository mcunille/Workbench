# Azure client ingress policy

Every deployment requires `ingressPolicy`. Omitting it fails ARM parameter validation;
there is no implicit public access default. The two supported values are:

```json
{ "mode": "Restricted", "allowCidrs": ["198.51.100.7/32"] }
```

```json
{ "mode": "Public", "allowCidrs": [] }
```

Replace the documentation address with the approved client IPv4 CIDRs. Restricted
mode requires at least one CIDR and emits an ACA `Allow` rule for each entry. An empty
ACA allow list permits all clients, so an empty Restricted policy is rejected by both
the parameter checker and the compiled ARM schema. Public mode requires an explicitly
empty list. The checker also rejects malformed, noncanonical, duplicate, IPv6, and
universal `/0` CIDRs. Run `infra/azure/validate-parameters.ps1 -ParametersFile <file>`
before deployment; ACA performs its own IP restriction validation at deployment time.
The template also makes universal IPv4/IPv6 `/0` entries invalid for direct ARM callers;
it never drops an invalid entry and accidentally produces an unrestricted empty list.

`publishIngress` independently selects external versus environment-internal ingress.
`activate = false` leaves ingress disabled for identity/bootstrap provisioning. The
example uses Restricted loopback (`127.0.0.1/32`) as a fail-closed bootstrap placeholder;
replace it with the approved operator/client CIDRs before exposing the application.
`proxyTrustMode` configures trust in forwarded address/protocol metadata and does not
grant client network access.

Before the first deployment using this contract, capture the existing app's
`configuration.ingress.ipSecurityRestrictions` through an authorized read-only Azure
query and carry every approved Allow CIDR into the deployment parameters. Preserve
the serving named revision traffic, publication setting, HTTPS-only setting, and
custom domain certificate. Review differences before applying: the template owns
the complete ingress object and will replace out-of-band restrictions. Introducing
Public mode is an explicit access-policy change requiring the usual publication
authorization. This runbook does not authorize changing live traffic or policy.

Run `infra/azure/test-parameters.ps1`, compile `infra/azure/main.bicep`, and run
`infra/azure/test-ingress-policy.ps1 -TemplateFile <compiled-main.json>` for local
contract checks. These checks inspect compiled ARM constraints and wiring; they do
not deploy Azure resources or establish that an operator can reach the live app.
