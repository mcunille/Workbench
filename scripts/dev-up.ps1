#Requires -Version 7.4
[CmdletBinding()]
param([string]$TenantName='Local Workbench', [string]$AdminEmail='admin@example.test', [switch]$Json)
. "$PSScriptRoot/dev-environment/Lifecycle.ps1"
$output = @(Invoke-DevCommand up (Split-Path $PSScriptRoot -Parent) $TenantName $AdminEmail '' -Json:$Json)
$code = $output[-1]
if ($output.Count -gt 1) { $output[0..($output.Count-2)] | Write-Output }
exit $code
