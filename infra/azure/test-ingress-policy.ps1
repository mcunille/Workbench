# Copyright (c) 2026 The White Stag Collection.
param([Parameter(Mandatory)][string] $TemplateFile)
$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath $TemplateFile -Raw | ConvertFrom-Json -AsHashtable

function Assert-PolicySchema($Template) {
    # GIVEN a compiled ARM deployment WHEN parameters bypass the PowerShell wrapper
    # THEN the required discriminator and array bounds still fail closed.
    $parameter = $Template.parameters.ingressPolicy
    if (-not $parameter -or $parameter.Contains('defaultValue')) { throw 'Ingress policy must be explicitly supplied.' }
    $policy = $Template.definitions[$parameter.'$ref'.Split('/')[-1]]
    if ($policy.discriminator.propertyName -cne 'mode' -or $policy.discriminator.mapping.Count -ne 2) {
        throw 'Ingress requires an explicit Restricted/Public discriminator.'
    }
    foreach ($mode in @('Restricted', 'Public')) {
        $reference = $policy.discriminator.mapping[$mode].'$ref'
        if (-not $reference) { throw "Missing $mode policy." }
        $branch = $Template.definitions[$reference.Split('/')[-1]]
        if ($branch.additionalProperties -ne $false -or $branch.properties.mode.allowedValues[0] -cne $mode) {
            throw 'Policy must reject unknown properties and mode values.'
        }
        $cidrs = $branch.properties.allowCidrs
        if ($cidrs.type -ne 'array' -or $cidrs.nullable) { throw 'Client CIDRs must be a required array.' }
        if ($mode -eq 'Restricted' -and ($cidrs.minLength -ne 1 -or $cidrs.items.type -ne 'string')) {
            throw 'Restricted policy must reject an empty client list at the ARM boundary.'
        }
        if ($mode -eq 'Public' -and ($cidrs.items -ne $false -or @($cidrs.prefixItems).Count -ne 0)) {
            throw 'Public policy must require an explicitly empty list.'
        }
    }
}
Assert-PolicySchema $template
$deployment = $template.resources.workloads
$workloads = $deployment.properties.template
Assert-PolicySchema $workloads
if ($deployment.properties.parameters.ingressPolicy.value -cne "[parameters('ingressPolicy')]") {
    throw 'Root policy must reach the workload unchanged.'
}
# GIVEN repeated deployments WHEN the ingress object is replaced
# THEN the complete Allow rules and existing release protections are included.
$rules = @($workloads.variables.copy | Where-Object name -eq 'clientIngressRules')
if ($rules.Count -ne 1 -or $rules[0].count -cne "[length(parameters('ingressPolicy').allowCidrs)]" -or
    $rules[0].input.action -cne 'Allow' -or
    -not $rules[0].input.ipAddressRange.Contains("int(last(split(parameters('ingressPolicy').allowCidrs[copyIndex('clientIngressRules')], '/'))), 0") -or
    -not $rules[0].input.ipAddressRange.Contains("'invalid-restricted-cidr'") -or
    -not $rules[0].input.ipAddressRange.Contains("parameters('ingressPolicy').allowCidrs[copyIndex('clientIngressRules')]") ) {
    throw 'Every configured client CIDR must produce an Allow rule.'
}
$ingress = $workloads.resources.web.properties.configuration.ingress
foreach ($contract in @(
    "if(parameters('activate'), createObject(",
    "'external', parameters('publishIngress')",
    "'ipSecurityRestrictions', variables('clientIngressRules')",
    "'allowInsecure', false()",
    "'traffic', parameters('releaseTraffic')",
    "'certificateId', parameters('customDomainCertificateId')",
    ', null())]'
)) {
    if (-not $ingress.Contains($contract)) { throw "Compiled ingress lost contract: $contract" }
}
Write-Host 'Compiled ingress requires explicit access policy and preserves Allow rules, private bootstrap, HTTPS, certificate, and named traffic wiring.'
