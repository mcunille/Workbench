[CmdletBinding()]
param([ValidateSet('None','OwnershipLabels','CachedImage','UnchangedImage','DatabaseReadiness','ImageAvailability')][string]$Mutation = 'None')
# Contract tests run lifecycle decisions without starting Docker or touching retained user state.
$ErrorActionPreference = 'Stop'
$lifecyclePath = Join-Path $PSScriptRoot '../../scripts/dev-environment/Lifecycle.ps1'
$failures = [Collections.Generic.List[string]]::new()
function Assert-Contract($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Rejected([scriptblock]$Action, [string]$Message, [string]$ExpectedError = "") {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true; if ($ExpectedError) { Assert-Contract ($_.Exception.Message.Contains($ExpectedError)) "Unexpected rejection: $($_.Exception.Message)" } }
    Assert-Contract $rejected $Message
}
function New-TestContext {
    $owner = @{Root='C:/lifecycle-fixture/checkout';GitDirectory='C:/lifecycle-fixture/git/worktrees/checkout'}
    @{ Owner=$owner; Root='C:/lifecycle-fixture/checkout/.dev-environment'; State=@{
        Version=1; EnvironmentId='dev-0123456789abcdef0123456789abcdef'; Owner=$owner
        Phase='ready'; Resources=@{}; Image=('sha256:' + ('a' * 64)); Port=45001
        Build=@{SourceHash='unchanged';Image=('sha256:' + ('a' * 64))}; InstallationId='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
    } }
}
function Test-Scenario([string]$Name, [scriptblock]$Body) {
    try {
        & {
            . $lifecyclePath
            # Mutations change only these process-local function definitions, never repository source.
            if ($Mutation -eq 'OwnershipLabels') { Set-Item Function:Assert-DevResourceLabels {} }
            if ($Mutation -eq 'ImageAvailability') {
                $original = (Get-Command Start-DevEnvironmentCore).Definition
                $fragment = ' -and $Context.State.Build.Image -cin @(Invoke-DevDocker $Context @(''image'',''ls'',''--all'',''--no-trunc'',''--quiet''))'
                Assert-Contract ($original.Contains($fragment)) 'Image availability mutation target changed.'
                Set-Item Function:Start-DevEnvironmentCore ([scriptblock]::Create($original.Replace($fragment, '')))
            }
            if ($Mutation -in @('CachedImage','UnchangedImage')) {
                $original = (Get-Command Start-DevEnvironmentCore).Definition
                $from = if ($Mutation -eq 'CachedImage') { '{ $Context.State.Build.Image } else { Build-DevImage' } else { ' -and $Context.State.Image -ceq $Context.State.Build.Image' }
                $to = if ($Mutation -eq 'CachedImage') { '{ $Context.State.Image } else { Build-DevImage' } else { '' }
                Assert-Contract ($original.Contains($from)) 'Mutation target changed; update the explicit mutation instead of claiming coverage.'
                Set-Item Function:Start-DevEnvironmentCore ([scriptblock]::Create($original.Replace($from, $to)))
            }
            if ($Mutation -eq 'DatabaseReadiness') {
                $original = (Get-Command Initialize-DevDatabase).Definition
                $fragment = "IF DB_ID(N'Workbench') IS NOT NULL EXEC(N'USE [Workbench]; SELECT 1;'); "
                Assert-Contract ($original.Contains($fragment)) 'Database readiness mutation target changed.'
                Set-Item Function:Initialize-DevDatabase ([scriptblock]::Create($original.Replace($fragment, '')))
            }
            & $Body
        }
        Write-Host "PASS $Name"
    }
    catch { $failures.Add("${Name}: $($_.Exception.Message)"); Write-Host "FAIL ${Name}: $($_.Exception.Message)" }
}

Test-Scenario 'Unchanged ready preview performs no build or service mutation' {
    # GIVEN a healthy preview of identical current source.
    $context = New-TestContext
    function Assert-DevResources($Context) {}
    function Get-DevSource($Repository) { @{Hash='unchanged'} }
    function Get-DevStatus($Context) { @{Ready=$true} }
    function Get-DevResource($Context, $Kind, $Key) { if ($Key -in @("app","sql")) { @{Id="$Key-id";State=@{Running=$true}} } }
    function Set-DevPhase($Context, $Phase) { $Context.State.Phase = $Phase }
    function Build-DevImage { throw 'Unexpected rebuild of unchanged preview.' }
    function Remove-DevContainer { throw 'Unexpected interruption of unchanged preview.' }
    function Invoke-DevDocker { throw 'Unexpected Docker mutation.' }
    # WHEN startup is requested again THEN the existing ready preview is reused.
    Start-DevEnvironment $context
    Assert-Contract ($context.State.Phase -ceq 'ready') 'Preview did not retain ready state.'
}

Test-Scenario 'Build failure preserves the previous running preview and provenance' {
    # GIVEN an existing healthy preview and changed source that fails compilation.
    $context = New-TestContext
    $image = $context.State.Image
    $mutations = [Collections.Generic.List[string]]::new()
    function Assert-DevResources($Context) {}
    function Get-DevSource($Repository) { @{Hash='changed'} }
    function Get-DevResource($Context, $Kind, $Key) { if ($Key -in @('app','sql')) { @{Id="$Key-id";State=@{Running=$true}} } }
    function Write-DevState {}
    function Build-DevImage($Context) { $Context.State.Phase = 'build'; throw 'Injected compilation failure.' }
    function Remove-DevContainer { throw 'The existing application must not be removed before a successful build.' }
    function Invoke-DevDocker($Context, $Arguments) { $mutations.Add(($Arguments -join " ")) }
    # WHEN startup fails THEN the prior immutable image and source evidence remain intact.
    Assert-Rejected { Start-DevEnvironment $context } 'Compilation failure was hidden.' 'Injected compilation failure.'
    Assert-Contract ($context.State.Image -ceq $image -and $context.State.Build.SourceHash -ceq 'unchanged') 'Build failure replaced prior provenance.'
    Assert-Contract ($mutations.Count -eq 0) 'Build failure attempted to modify the prior runtime.'
}

Test-Scenario 'Stop preserves volumes and stops only validated running container IDs' {
    # GIVEN running SQL/application containers and retained data volumes.
    $context = New-TestContext
    $context.State.Resources = @{app='app-id';sql='sql-id';blobs='blob-created';'sql-data'='sql-created'}
    $before = $context.State.Resources | ConvertTo-Json -Compress
    $calls = [Collections.Generic.List[string]]::new()
    function Assert-DevResources($Context) {}
    function Set-DevPhase($Context, $Phase) { $Context.State.Phase = $Phase }
    function Get-DevResource($Context, $Kind, $Key) {
        if ($Kind -ceq 'container' -and $Key -in @('app','sql')) { @{Id="$Key-id";State=@{Running=$true}} }
    }
    function Invoke-DevDocker($Context, $Arguments) { $calls.Add(($Arguments -join ' ')) }
    # WHEN stopping THEN only those exact container IDs receive stop commands and all retained authority survives.
    Stop-DevEnvironment $context
    Assert-Contract ($calls.Count -eq 2 -and $calls.Contains('container stop --time 30 app-id') -and $calls.Contains('container stop --time 30 sql-id')) 'Stop touched unexpected resources.'
    Assert-Contract (($context.State.Resources | ConvertTo-Json -Compress) -ceq $before) 'Stop discarded retained resource identities.'
    Assert-Contract ($context.State.Phase -ceq 'stopped') 'Stop did not report completion.'
}

Test-Scenario 'Wrong destroy confirmation never reaches Docker or state writes' {
    # GIVEN a valid environment but an identifier copied from another preview.
    $context = New-TestContext
    function Assert-DevResources { throw 'Docker must not be queried before exact destroy confirmation.' }
    function Write-DevState { throw 'State must not change on incorrect confirmation.' }
    # WHEN destroy is requested THEN authority is rejected before mutation.
    Assert-Rejected { Destroy-DevEnvironment $context 'dev-fedcba9876543210fedcba9876543210' } 'Wrong environment ID was accepted.'
    Assert-Contract ($context.State.Phase -ceq 'ready') 'Incorrect destroy changed phase.'
}

Test-Scenario 'Foreign resource labels prevent any stop or deletion' {
    # GIVEN a container with the expected name but labels belonging to another checkout.
    $context = New-TestContext
    $mutations = [Collections.Generic.List[string]]::new()
    function Invoke-DevDocker($Context, $Arguments) {
        if ($Arguments[1] -ceq 'ls') { return 'workbench-dev-0123456789abcdef0123456789abcdef-app' }
        if ($Arguments[1] -ceq 'inspect') { return '[{"Id":"foreign-id","Config":{"Labels":{"workbench.purpose":"development","workbench.environment":"foreign","workbench.owner":"foreign"}},"State":{"Running":true}}]' }
        $mutations.Add(($Arguments -join ' '))
    }
    function Write-DevState {}
    # WHEN stopping or destroying THEN neither operation can modify the foreign resource.
    Assert-Rejected { Stop-DevEnvironment $context } 'Foreign stop was accepted.' 'ownership does not match'
    Assert-Rejected { Destroy-DevEnvironment $context $context.State.EnvironmentId } 'Foreign destroy was accepted.' 'ownership does not match'
    Assert-Contract ($mutations.Count -eq 0) 'Foreign resource was mutated.'
}

Test-Scenario 'Replaced resource identity is rejected despite matching labels' {
    # GIVEN the correct labels on a container recreated outside the environment operation.
    $context = New-TestContext
    $context.State.Resources.app = 'original-id'
    $labels = Get-DevLabels $context.State
    $replacement = @(@{Id='replacement-id';Config=@{Labels=$labels}}) | ConvertTo-Json -Depth 5 -Compress
    function Invoke-DevDocker($Context, $Arguments) {
        if ($Arguments[1] -ceq 'ls') { return 'workbench-dev-0123456789abcdef0123456789abcdef-app' }
        if ($Arguments[1] -ceq 'inspect') { return $replacement }
        throw 'Replacement must not be mutated.'
    }
    # WHEN resolving runtime authority THEN matching names and labels cannot replace a recorded immutable ID.
    Assert-Rejected { Get-DevResource $context container app } 'Recreated resource was silently adopted.'
    Assert-Contract ($context.State.Resources.app -ceq 'original-id') 'Original ownership evidence was overwritten.'
}

Test-Scenario 'Independent previews have disjoint containers volumes networks and browser IDs' {
    # GIVEN two independently identified checkouts.
    $first = New-TestContext
    $second = New-TestContext
    $second.State.EnvironmentId = 'dev-fedcba9876543210fedcba9876543210'
    $second.Owner.Root = 'C:/lifecycle-fixture/second'
    $second.Owner.GitDirectory = 'C:/lifecycle-fixture/git/worktrees/second'
    # WHEN generating their complete runtime topology THEN every persistent/runtime resource is distinct.
    $left = New-DevCompose $first
    $right = New-DevCompose $second
    Assert-Contract ($left.services.app.container_name -cne $right.services.app.container_name) 'Application container collision.'
    Assert-Contract ($left.services.sql.container_name -cne $right.services.sql.container_name) 'SQL container collision.'
    Assert-Contract ($left.volumes.blobs.name -cne $right.volumes.blobs.name) 'Photo volume collision.'
    Assert-Contract ($left.volumes.'sql-data'.name -cne $right.volumes.'sql-data'.name) 'SQL volume collision.'
    Assert-Contract ($left.networks.dependencies.name -cne $right.networks.dependencies.name) 'Network collision.'
    Assert-Contract ($left.services.app.environment.Development__EnvironmentId -cne $right.services.app.environment.Development__EnvironmentId) 'Browser identity collision.'
}

Test-Scenario 'Missing retained volume is never recreated empty' {
    # GIVEN retained SQL volume authority whose actual resource has gone missing.
    $context = New-TestContext
    $context.State.Resources.'sql-data' = 'original-created-time'
    $creates = [Collections.Generic.List[string]]::new()
    function Get-DevResource($Context, $Kind, $Key) { if ($Key -ceq 'network') { @{Id='network-id'} } }
    function Register-DevResource($Context, $Kind, $Key) { @{Id='network-id'} }
    function Invoke-DevDocker($Context, $Arguments) { $creates.Add(($Arguments -join ' ')) }
    function Write-DevState {}
    # WHEN starting retained infrastructure THEN fail instead of creating an empty substitute.
    Assert-Rejected { Initialize-DevResources $context } 'Missing retained SQL volume was accepted.'
    Assert-Contract ($creates.Count -eq 0) 'An empty replacement volume was created.'
}

Test-Scenario 'Startup failure stops SQL newly started by the failed attempt' {
    # GIVEN a fresh environment with no running services and a migration failure after SQL starts.
    $context = New-TestContext
    $context.State.Build = @{}
    $runtime = @{SqlRunning=$false}
    function Assert-DevResources($Context) {}
    function Get-DevSource($Repository) { @{Hash='new-source'} }
    function Build-DevImage { 'sha256:' + ('b' * 64) }
    function Set-DevPhase($Context, $Phase) { $Context.State.Phase = $Phase }
    function Write-DevState {}
    function Remove-DevContainer {}
    function Initialize-DevSecrets {}
    function Initialize-DevResources {}
    function Write-DevCompose {}
    function Get-DevResource($Context, $Kind, $Key) {
        if ($Key -ceq 'sql' -and $runtime.SqlRunning) { @{Id='new-sql-id';State=@{Running=$true}} }
    }
    function Register-DevResource($Context, $Kind, $Key) { Get-DevResource $Context $Kind $Key }
    function Invoke-DevCompose($Context, $Arguments) {
        if ($Arguments -contains 'sql') { $runtime.SqlRunning = $true }
    }
    function Invoke-DevDocker($Context, $Arguments) {
        if ($Arguments -contains 'stop' -and $Arguments -contains 'new-sql-id') { $runtime.SqlRunning = $false }
    }
    function Initialize-DevDatabase { throw 'Injected migration failure.' }
    # WHEN startup aborts THEN the new SQL service is stopped while retained data remains untouched.
    Assert-Rejected { Start-DevEnvironment $context } 'Migration failure was hidden.' 'Injected migration failure.'
    Assert-Contract (-not $runtime.SqlRunning) 'New SQL remains running after failed startup.'
}

Test-Scenario 'Source inventory failure preserves the existing running preview' {
    # GIVEN a running preview and a source inventory failure before build or refresh begins.
    $context = New-TestContext
    $calls = [Collections.Generic.List[string]]::new()
    function Assert-DevResources($Context) {}
    function Get-DevResource($Context, $Kind, $Key) {
        if ($Key -in @('app','sql')) { @{Id="$Key-original-id";State=@{Running=$true}} }
    }
    function Get-DevSource { throw 'Injected source inventory failure.' }
    function Invoke-DevDocker($Context, $Arguments) { $calls.Add(($Arguments -join ' ')) }
    function Write-DevState {}
    # WHEN startup cannot inspect source THEN the unrelated healthy runtime is not stopped.
    Assert-Rejected { Start-DevEnvironment $context } 'Source inventory failure was hidden.' 'Injected source inventory failure.'
    Assert-Contract ($calls.Count -eq 0) 'Failure before refresh stopped a pre-existing service.'
}

Test-Scenario 'Corrupt or copied state cannot authorize runtime operations' {
    # GIVEN a private disposable state location and valid authority for one checkout.
    $context = New-TestContext
    $directory = Join-Path ([IO.Path]::GetTempPath()) ('workbench-dev-lifecycle-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $directory | Out-Null
        $path = Join-Path $directory 'state.json'
        # WHEN the manifest is malformed THEN fail instead of interpreting it as a fresh environment.
        [IO.File]::WriteAllText($path, '{broken')
        Assert-Rejected { Read-DevState $directory $context.Owner } 'Malformed state was accepted.'
        # WHEN well-formed state belongs to a different checkout THEN fail without rewriting its authority.
        $context.State.Owner = @{Root='C:/different-checkout';GitDirectory='C:/different-git'}
        $original = $context.State | ConvertTo-Json -Depth 10
        [IO.File]::WriteAllText($path, $original)
        Assert-Rejected { Read-DevState $directory $context.Owner } 'Copied state was accepted.' 'belongs to another checkout'
        Assert-Contract ([IO.File]::ReadAllText($path) -ceq $original) 'Rejected state was modified.'
    } finally {
        $resolved = [IO.Path]::GetFullPath($directory)
        if ([IO.Path]::GetDirectoryName($resolved) -cne [IO.Path]::GetTempPath().TrimEnd('\','/') -or
            [IO.Path]::GetFileName($resolved) -cnotmatch '^workbench-dev-lifecycle-[a-f0-9]{32}$') { throw 'Unexpected test cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
Test-Scenario 'Reverted source restores the last verified image after a failed refresh' {
    # GIVEN source A's successful provenance and candidate image B left behind by a failed refresh.
    $context = New-TestContext
    $verifiedImage = $context.State.Build.Image
    $context.State.Image = 'sha256:' + ('b' * 64)
    function Assert-DevResources($Context) {}
    function Get-DevSource($Repository) { @{Hash='unchanged'} }
    function Get-DevStatus($Context) { @{Ready=$true} }
    function Build-DevImage { throw 'Previously verified source must use its own cached image.' }
    function Invoke-DevDocker($Context, $Arguments) {
        Assert-Contract (($Arguments -join ' ') -ceq 'image ls --all --no-trunc --quiet') 'Unexpected image lookup.'
        $verifiedImage
    }
    function Set-DevPhase($Context, $Phase) { $Context.State.Phase = $Phase }
    function Remove-DevContainer {}
    function Write-DevState {}
    function Initialize-DevSecrets { throw 'Reached refresh after selecting image.' }
    # WHEN source is reverted to A THEN B cannot pass unchanged reuse or become A's cached image.
    Assert-Rejected { Start-DevEnvironmentCore $context } 'Wrong image was reused as an unchanged preview.' 'Reached refresh after selecting image.'
    Assert-Contract ($context.State.Image -ceq $verifiedImage) 'Failed candidate image was selected for reverted source.'
}

Test-Scenario 'Missing cached image rebuilds reverted source before touching retained resources' {
    # GIVEN reverted source A, failed candidate B, and a pruned cached image A.
    $context = New-TestContext
    $context.State.Image = 'sha256:' + ('b' * 64)
    $context.State.Resources = @{blobs='retained-blobs';'sql-data'='retained-sql'}
    $retained = $context.State.Resources | ConvertTo-Json -Compress
    $events = [Collections.Generic.List[string]]::new()
    $rebuiltImage = 'sha256:' + ('c' * 64)
    function Assert-DevResources {}
    function Get-DevSource { @{Hash='unchanged'} }
    function Get-DevStatus { @{Ready=$false} }
    function Invoke-DevDocker($Context, $Arguments) {
        Assert-Contract (($Arguments -join ' ') -ceq 'image ls --all --no-trunc --quiet') 'Unexpected image lookup.'
        $context.State.Image
    }
    function Build-DevImage($Context, $Source) {
        Assert-Contract ($Source.Hash -ceq 'unchanged') 'Rebuild used the wrong source.'
        $events.Add('build'); $rebuiltImage
    }
    function Set-DevPhase($Context, $Phase) { $Context.State.Phase = $Phase }
    function Remove-DevContainer { $events.Add('remove-app') }
    function Write-DevState {}
    function Initialize-DevSecrets { throw 'Reached refresh after selecting image.' }
    # WHEN restarting THEN rebuild current source before replacing the app and preserve data authority.
    Assert-Rejected { Start-DevEnvironmentCore $context } 'Refresh did not proceed.' 'Reached refresh after selecting image.'
    Assert-Contract (($events -join ',') -ceq 'build,remove-app') 'Missing cache did not rebuild before app removal.'
    Assert-Contract ($context.State.Image -ceq $rebuiltImage) 'Refresh selected the missing cached image.'
    Assert-Contract (($context.State.Resources | ConvertTo-Json -Compress) -ceq $retained) 'Recovery changed retained data authority.'
}

Test-Scenario 'Image inventory failure preserves the previous preview without rebuilding' {
    # GIVEN matching source and a Docker engine failure while checking its cached image.
    $context = New-TestContext
    $before = $context.State | ConvertTo-Json -Depth 10 -Compress
    function Assert-DevResources {}
    function Get-DevSource { @{Hash='unchanged'} }
    function Get-DevStatus { @{Ready=$false} }
    function Invoke-DevDocker { throw 'Injected image inventory failure.' }
    function Set-DevPhase { throw 'Refresh started without checking image inventory.' }
    function Build-DevImage { throw 'Inventory failure must not trigger a build.' }
    function Remove-DevContainer { throw 'Inventory failure must not remove the application.' }
    # WHEN cache availability cannot be determined THEN surface the failure without changing state.
    Assert-Rejected { Start-DevEnvironmentCore $context } 'Inventory failure was hidden.' 'Injected image inventory failure.'
    Assert-Contract (($context.State | ConvertTo-Json -Depth 10 -Compress) -ceq $before) 'Inventory failure modified preview state.'
}

Test-Scenario 'Stopped environment remains reportable when source inventory fails' {
    # GIVEN a removable preview whose checkout no longer has a Dockerfile.
    $context = New-TestContext
    function Assert-DevResources($Context) {}
    function Get-DevResource {}
    function Write-DevState {}
    function Get-DevSource { throw 'Missing Dockerfile: private-source-diagnostic-sentinel' }
    # WHEN stop completes and its status is produced THEN source failure does not turn cleanup into failure.
    Stop-DevEnvironment $context
    $status = Get-DevStatus $context
    Assert-Contract ($status.Phase -ceq 'stopped' -and -not $status.Ready) 'Stopped state was lost.'
    Assert-Contract ($null -eq $status.SourceChanged) 'Unavailable source was reported as a known fingerprint comparison.'
    Assert-Contract (-not [string]::IsNullOrWhiteSpace($status.SourceError)) 'Source inventory limitation was hidden.'
    Assert-Contract (-not $status.SourceError.Contains('private-source-diagnostic-sentinel')) 'Raw source exception was exposed.'
}

Test-Scenario 'Destroyed environment remains reportable when source inventory fails' {
    # GIVEN an owned environment with no remaining runtime resources and unavailable source inventory.
    $context = New-TestContext
    function Assert-DevResources($Context) {}
    function Get-DevResource {}
    function Write-DevState {}
    function Get-DevSource { throw 'Missing Dockerfile: private-source-diagnostic-sentinel' }
    # WHEN exact-ID destruction completes THEN its status still reports successful cleanup.
    Destroy-DevEnvironment $context $context.State.EnvironmentId
    $status = Get-DevStatus $context
    Assert-Contract ($status.Phase -ceq 'destroyed' -and -not $status.Ready) 'Destroyed state was lost.'
    Assert-Contract ($null -eq $status.SourceChanged) 'Unavailable source was reported as a known fingerprint comparison.'
    Assert-Contract (-not [string]::IsNullOrWhiteSpace($status.SourceError)) 'Source inventory limitation was hidden.'
    Assert-Contract (-not $status.SourceError.Contains('private-source-diagnostic-sentinel')) 'Raw source exception was exposed.'
}
Test-Scenario 'Missing retained database is refused without issuing CREATE DATABASE' {
    # GIVEN an already provisioned environment whose SQL engine is reachable but retained database is missing.
    $context = New-TestContext
    $context.State.Provisioned = $true
    $batches = [Collections.Generic.List[string]]::new()
    function Set-DevPhase($Context, $Phase) { $Context.State.Phase = $Phase }
    function Invoke-DevSql($Context, $Sql) {
        $batches.Add($Sql)
        if ($Sql -match '(?i)sys\.databases' -and $Sql -match '(?i)state_desc') { return $true }
        return $false
    }
    function Invoke-DevDatabase { throw 'Missing retained database must fail before migration or provisioning.' }
    # WHEN initialization checks the retained installation THEN it refuses rather than replacing missing data.
    Assert-Rejected { Initialize-DevDatabase $context } 'Missing retained database was accepted.' 'retained data is never replaced automatically'
    Assert-Contract ($batches.Count -eq 2) 'Unexpected database operations after the failed retained-database check.'
    Assert-Contract ($batches[1] -notmatch '(?i)CREATE\s+DATABASE') 'Retained database was recreated empty.'
    Assert-Contract ($batches[1] -match "(?i)IF\s+DB_ID\(N'Workbench'\)\s+IS\s+NULL\s+THROW") 'Missing retained database does not fail closed in SQL.'
}
Test-Scenario 'Retained database startup waits for user database recovery without reconfiguring it' {
    # GIVEN a provisioned SQL instance whose master is responsive while the user database is recovering.
    $context = New-TestContext
    $context.State.Provisioned = $true
    $batches = [Collections.Generic.List[string]]::new()
    function Set-DevPhase($Context, $Phase) { $Context.State.Phase = $Phase }
    $recovery = @{Checks=0}
    function Start-Sleep {}
    function Invoke-DevSql($Context, $Sql) {
        $batches.Add($Sql)
        if ($Sql -match '(?i)ONLINE' -and $Sql -match '(?i)state_desc|DATABASEPROPERTYEX') {
            $recovery.Checks++
            return $recovery.Checks -gt 1
        }
        return $true
    }
    function Invoke-DevDatabase { throw 'Reached inspection after SQL readiness.' }
    # WHEN startup reaches inspection THEN readiness checked the user database and did not repeat first-install DDL.
    Assert-Rejected { Initialize-DevDatabase $context } 'Startup did not reach database inspection.' 'Reached inspection after SQL readiness.'
    $sql = $batches -join "`n"
    Assert-Contract ($sql -notmatch '(?i)ALTER\s+DATABASE|sp_configure|RECONFIGURE') 'Retained startup reapplied database/server configuration.'
    Assert-Contract ($sql -match '(?i)Workbench' -and $sql -match '(?i)ONLINE' -and $sql -match '(?i)state_desc|DATABASEPROPERTYEX') 'Readiness only checked master; user database recovery was not checked.'
    Assert-Contract ($batches[0] -match '(?i)USE\s+\[Workbench\]\s*;\s*SELECT\s+1') 'Readiness inspected metadata without opening the retained Workbench database.'
    Assert-Contract ($recovery.Checks -eq 2) 'Startup did not retry while the retained user database was recovering.'
}
if ($failures.Count) { throw ($failures -join "`n") }
Write-Host 'Development lifecycle contracts passed.'
