param prefix string
param workerId string
param migrationId string
param alertEmails array
param monthlyBudget int
param budgetStartDate string
param location string
param logsId string
param workerEnabled bool
param webEnabled bool

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${prefix}-operations'
  location: 'global'
  properties: {
    groupShortName: 'workbench'
    enabled: true
    emailReceivers: [for (email, i) in alertEmails: { name: 'operator-${i}', emailAddress: email, useCommonAlertSchema: true }]
  }
}
var jobs = [workerId, migrationId]
resource failedJobs 'Microsoft.Insights/metricAlerts@2018-03-01' = [for (jobId, i) in jobs: {
  name: '${prefix}-failed-job-${i}'
  location: 'global'
  properties: {
    description: 'Investigate failed Workbench job executions; never replay identity payloads manually.'
    severity: 2
    enabled: true
    scopes: [jobId]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [{
        name: 'failed'
        metricNamespace: 'Microsoft.App/jobs'
        metricName: 'Executions'
        dimensions: [{ name: 'state', operator: 'Include', values: ['Failed'] }]
        operator: 'GreaterThan'
        threshold: 0
        timeAggregation: 'Maximum'
        criterionType: 'StaticThresholdCriterion'
      }]
    }
    actions: [{ actionGroupId: actionGroup.id }]
  }
}]
var logAlerts = [
  {
    name: 'queue-age'
    enabled: workerEnabled
    description: 'The latest worker status reports pending work older than five minutes.'
    query: replace(loadTextContent('../queries/queue-age.kql'), '__WORKER_NAME__', '${prefix}-worker')
  }
  {
    name: 'worker-status-missing'
    enabled: workerEnabled
    description: 'No valid worker queue status was ingested for ten minutes. Check execution, SQL and log ingestion.'
    query: replace(loadTextContent('../queries/worker-status-missing.kql'), '__WORKER_NAME__', '${prefix}-worker')
  }
  {
    name: 'readiness-failures'
    enabled: webEnabled
    description: 'A revision has recent, repeated platform readiness-probe failures spanning at least five minutes.'
    query: replace(loadTextContent('../queries/readiness-failures.kql'), '__WEB_NAME__', '${prefix}-web')
  }
]
resource logRules 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = [for alert in logAlerts: {
  name: '${prefix}-${alert.name}'
  location: location
  kind: 'LogAlert'
  properties: {
    displayName: '${prefix}: ${alert.name}'
    description: alert.description
    severity: 2
    enabled: alert.enabled
    scopes: [logsId]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT10M'
    // Tables do not exist during the first inactive bootstrap. Live query validation is a release gate.
    skipQueryValidation: true
    autoMitigate: true
    criteria: {
      allOf: [{
        query: alert.query
        timeAggregation: 'Count'
        operator: 'GreaterThan'
        threshold: 0
        failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
      }]
    }
    actions: { actionGroups: [actionGroup.id] }
  }
}]
resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: '${prefix}-monthly'
  properties: {
    category: 'Cost'
    amount: monthlyBudget
    timeGrain: 'Monthly'
    timePeriod: { startDate: budgetStartDate }
    notifications: {
      actual80: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 80
        thresholdType: 'Actual'
        contactEmails: alertEmails
        contactGroups: [actionGroup.id]
      }
      forecast100: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Forecasted'
        contactEmails: alertEmails
        contactGroups: [actionGroup.id]
      }
    }
  }
}
