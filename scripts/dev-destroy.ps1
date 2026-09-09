#Requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$EnvironmentId, [switch]$Json)
. "$PSScriptRoot/dev-environment/Lifecycle.ps1"
$output = @(Invoke-DevCommand destroy (Split-Path $PSScriptRoot -Parent) '' '' $EnvironmentId -Json:$Json)
$code = $output[-1]
if ($output.Count -gt 1) { $output[0..($output.Count-2)] | Write-Output }
exit $code
