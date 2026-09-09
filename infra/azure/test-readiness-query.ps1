# Copyright (c) 2026 The White Stag Collection.
param(
    [Parameter(Mandatory)][string] $WorkspaceId,
    [string] $QueryFile = (Join-Path $PSScriptRoot 'queries/readiness-failures.kql')
)
$ErrorActionPreference = 'Stop'
if (-not [Guid]::TryParse($WorkspaceId, [ref]([Guid]::Empty))) { throw 'WorkspaceId must be a UUID.' }

# GIVEN synthetic platform messages; no records are written to the workspace.
# AND each sustained failure has two events six minutes apart with a recent final event.
$fixture = @'
let ContainerAppSystemLogs_CL = datatable(Age:timespan, ContainerAppName_s:string, RevisionName_s:string, Reason_s:string, Log_s:string) [
7m, 'test-web', 'modern', 'ProbeFailed', 'Probe of Readiness failed with status code: 503',
1m, 'test-web', 'modern', 'ProbeFailed', 'Probe of Readiness failed with status code: 503',
7m, 'test-web', 'container', 'ProbeFailed', 'Container web failed readiness probe',
1m, 'test-web', 'container', 'ProbeFailed', 'Container web failed readiness probe',
7m, 'test-web', 'threshold', 'ProbeFailed', 'Readiness Probe of Readiness reached failure threshold 3, changing status to Failure.',
1m, 'test-web', 'threshold', 'ProbeFailed', 'Readiness Probe of Readiness reached failure threshold 3, changing status to Failure.',
7m, 'test-web', 'legacy', 'ProbeFailed', 'readiness probe failed',
1m, 'test-web', 'legacy', 'ProbeFailed', 'readiness probe failed',
7m, 'test-web', 'startup', 'ProbeFailed', 'Probe of StartUp failed with status code: ',
1m, 'test-web', 'startup', 'ProbeFailed', 'Probe of StartUp failed with status code: ',
7m, 'test-web', 'liveness', 'ProbeFailed', 'Probe of Liveness failed with status code: 503',
1m, 'test-web', 'liveness', 'ProbeFailed', 'Probe of Liveness failed with status code: 503',
7m, 'another-web', 'wrong-app', 'ProbeFailed', 'Probe of Readiness failed with status code: 503',
1m, 'another-web', 'wrong-app', 'ProbeFailed', 'Probe of Readiness failed with status code: 503',
7m, 'test-web', 'wrong-reason', 'ContainerStarted', 'Probe of Readiness failed with status code: 503',
1m, 'test-web', 'wrong-reason', 'ContainerStarted', 'Probe of Readiness failed with status code: 503',
9m, 'test-web', 'stale', 'ProbeFailed', 'Probe of Readiness failed with status code: 503',
3m, 'test-web', 'stale', 'ProbeFailed', 'Probe of Readiness failed with status code: 503',
3m, 'test-web', 'transient', 'ProbeFailed', 'Probe of Readiness failed with status code: 503',
1m, 'test-web', 'transient', 'ProbeFailed', 'Probe of Readiness failed with status code: 503',
7m, 'test-web', 'split-a', 'ProbeFailed', 'Probe of Readiness failed with status code: 503',
1m, 'test-web', 'split-b', 'ProbeFailed', 'Probe of Readiness failed with status code: 503',
12m, 'test-web', 'outside-window', 'ProbeFailed', 'Probe of Readiness failed with status code: 503',
1m, 'test-web', 'outside-window', 'ProbeFailed', 'Probe of Readiness failed with status code: 503'
] | extend TimeGenerated = now() - Age;
'@
# WHEN the actual checked-in alert query is evaluated by Azure's KQL engine.
$query = (Get-Content -LiteralPath $QueryFile -Raw).Replace('__WEB_NAME__', 'test-web')
$requestFile = [IO.Path]::GetTempFileName()
try {
    @{ query = "$fixture`n$query" } | ConvertTo-Json | Set-Content -LiteralPath $requestFile
    $response = az rest --method post --resource https://api.loganalytics.io --url "https://api.loganalytics.io/v1/workspaces/$WorkspaceId/query" --body "@$requestFile" --output json
    if ($LASTEXITCODE -ne 0) { throw 'KQL regression query failed.' }
    $result = $response | ConvertFrom-Json
    if ($result.error) { throw 'KQL returned an error or partial result.' }
    $actual = @($result.tables[0].rows | ForEach-Object { $_[0] } | Sort-Object)
    # THEN only sustained, recent readiness failures from the selected app alert.
    # AND startup/liveness, wrong source, stale/transient and cross-revision events do not alert.
    $expected = @('container', 'legacy', 'modern', 'threshold')
    if (@(Compare-Object $expected $actual).Count -ne 0) {
        throw "Readiness regression failed. Expected $($expected -join ', '); got $($actual -join ', ')."
    }
    'READINESS_KQL_REGRESSION_PASSED'
}
finally { Remove-Item -LiteralPath $requestFile -ErrorAction SilentlyContinue }
