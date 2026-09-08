targetScope = 'subscription'

param diagnosticName string
param workspaceId string

// Administrative events carry the control-plane change trail; no workload credentials are requested.
resource activityExport 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: diagnosticName
  properties: {
    workspaceId: workspaceId
    logs: [
      { category: 'Administrative', enabled: true }
      { category: 'Security', enabled: true }
      { category: 'Policy', enabled: true }
      { category: 'Alert', enabled: true }
    ]
  }
}
