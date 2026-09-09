function Invoke-DevDocker($Context, [string[]]$Arguments) {
    Invoke-LocalDocker $Context.Docker $Arguments
}
function Get-DevResourceName($Context, [string]$Key) { "workbench-$($Context.State.EnvironmentId)-$Key" }
function Get-DevResource($Context, [string]$Kind, [string]$Key) {
    $name = Get-DevResourceName $Context $Key
    $format = if ($Kind -eq 'container') { '{{.Names}}' } else { '{{.Name}}' }
    $arguments = @($Kind,'ls')
    if ($Kind -eq 'container') { $arguments += '-a' }
    $names = @(Invoke-DevDocker $Context ($arguments + @('--format',$format)))
    if ($name -cnotin $names) { return $null }
    $resource = @((Invoke-DevDocker $Context @($Kind,'inspect',$name) | Out-String | ConvertFrom-Json -AsHashtable))[0]
    $labels = if ($Kind -eq 'container') { $resource.Config.Labels } else { $resource.Labels }
    Assert-DevResourceLabels $Context.State $labels
    $identity = if ($Kind -eq 'volume') { $resource.CreatedAt } else { $resource.Id }
    $recorded = $Context.State.Resources[$Key]
    if ($recorded -and $recorded -cne $identity) { throw "Resource '$Key' was replaced outside this environment. Preserve state and inspect ownership." }
    return $resource
}
function Register-DevResource($Context, [string]$Kind, [string]$Key) {
    $resource = Get-DevResource $Context $Kind $Key
    if (-not $resource) { throw "Expected owned resource '$Key' is missing." }
    $Context.State.Resources[$Key] = if ($Kind -eq 'volume') { $resource.CreatedAt } else { $resource.Id }
    Write-DevState $Context
    return $resource
}
function Assert-DevResources($Context) {
    foreach ($key in @('app','sql','tool')) { Get-DevResource $Context container $key | Out-Null }
    Get-DevResource $Context network network | Out-Null
    foreach ($key in @('blobs','sql-data')) { Get-DevResource $Context volume $key | Out-Null }
}
function Remove-DevContainer($Context, [string]$Key) {
    $container = Get-DevResource $Context container $Key
    if ($container) { Invoke-DevDocker $Context @('container','rm','--force',$container.Id) | Out-Null }
    $Context.State.Resources.Remove($Key)
    Write-DevState $Context
}
function Initialize-DevResources($Context) {
    $labels = Get-DevLabels $Context.State
    $labelArguments = @(); foreach ($key in $labels.Keys) { $labelArguments += @('--label',"$key=$($labels[$key])") }
    foreach ($entry in @(@('network','network'),@('volume','sql-data'),@('volume','blobs'))) {
        $kind,$key = $entry
        if (-not (Get-DevResource $Context $kind $key)) {
            # A missing retained volume must never become a silent empty replacement.
            if ($Context.State.Resources[$key]) { throw "Retained resource '$key' is missing. Explicitly destroy/reset this disposable environment to start fresh." }
            Set-DevPhase $Context "create-$key"
            Invoke-DevDocker $Context (@($kind,'create') + $labelArguments + @((Get-DevResourceName $Context $key))) | Out-Null
        }
        Register-DevResource $Context $kind $key | Out-Null
    }
}
function Invoke-DevCompose($Context, [string[]]$Arguments) {
    Assert-DevResources $Context
    Invoke-DevDocker $Context (@('compose','--project-name',"workbench-$($Context.State.EnvironmentId)",'--file',"$($Context.Root)/compose.json") + $Arguments)
}
