function New-DevCompose($Context) {
    $state = $Context.State
    $project = "workbench-$($state.EnvironmentId)"
    $labels = Get-DevLabels $state
    $secrets = "$($Context.Root)/secrets"
    $app = @{
        image=$state.Image; pull_policy='never'; container_name="$project-app"; labels=$labels
        init=$true; user='1654:1654'; read_only=$true; restart='unless-stopped'; stop_grace_period='30s'
        cap_drop=@('ALL'); security_opt=@('no-new-privileges:true'); networks=@('dependencies')
        tmpfs=@('/tmp:rw,noexec,nosuid,size=64m,uid=1654,gid=1654')
        ports=@(@{target=8080;published=[string]$state.Port;host_ip='127.0.0.1';protocol='tcp'})
        environment=@{
            ASPNETCORE_ENVIRONMENT='Development'; Development__EnvironmentId=$state.EnvironmentId
            ConnectionStrings__WorkbenchFile='/run/secrets/web-connection'
            WORKBENCH_TENANT_CONTEXT_PROOF_KEY_FILE='/run/secrets/tenant-proof'
            Storage__Provider='FileSystem'; Storage__Root='/var/lib/workbench/blobs'; Storage__DurableVolume='true'
            Storage__InstallationId=$state.InstallationId; Deployment__Replicas='1'
            AllowedHosts='localhost;127.0.0.1'; WORKBENCH_HEALTH_HOST='localhost'
        }
        volumes=@((New-SetupMount 'blobs' '/var/lib/workbench/blobs' $false 'volume'),
            (New-SetupMount "$secrets/web-connection" '/run/secrets/web-connection'),
            (New-SetupMount "$secrets/tenant-proof" '/run/secrets/tenant-proof'))
    }
    $sql = @{
        image='mcr.microsoft.com/mssql/server:2022-CU20-ubuntu-22.04'; container_name="$project-sql"; hostname='sql'
        labels=$labels; restart='unless-stopped'; networks=@('dependencies'); environment=@{ ACCEPT_EULA='Y'; MSSQL_PID='Developer' }
        cap_drop=@('ALL'); cap_add=@('NET_BIND_SERVICE'); security_opt=@('no-new-privileges:true')
        entrypoint=@('/bin/bash','-ec'); command=@('export MSSQL_SA_PASSWORD="$$(cat /run/secrets/sql-bootstrap-password)"; exec /opt/mssql/bin/sqlservr')
        volumes=@((New-SetupMount 'sql-data' '/var/opt/mssql' $false 'volume'),
            (New-SetupMount "$secrets/sql-bootstrap-password" '/run/secrets/sql-bootstrap-password'))
    }
    @{ name=$project; services=@{app=$app;sql=$sql}
        networks=@{dependencies=@{name="$project-network";external=$true;labels=$labels}}
        volumes=@{blobs=@{name="$project-blobs";external=$true;labels=$labels};'sql-data'=@{name="$project-sql-data";external=$true;labels=$labels}}
    }
}
function Write-DevCompose($Context) {
    $config = New-DevCompose $Context
    # External resources are created and ownership-checked separately; Compose disallows labels on them.
    foreach ($resource in @($config.networks.dependencies,$config.volumes.blobs,$config.volumes.'sql-data')) { $resource.Remove('labels') }
    [IO.File]::WriteAllText("$($Context.Root)/compose.json", ($config | ConvertTo-Json -Depth 20))
}
