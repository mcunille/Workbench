param location string
param prefix string
param environmentId string
param image string
param registryServer string
param registryPullIdentityId string
param activate bool
param workerEnabled bool
param publishIngress bool
param releaseTraffic array
param publicOrigin string
param publicHost string
param customDomainCertificateId string
param trustedProxyAddresses array
param trustedProxyNetworks array
param installationId string
param sqlHost string
param databaseName string
param containerUri string
param vaultUri string
param maxReplicas int
param workerSchedule string
param smtpHost string
param smtpPort int
param smtpUsername string
param smtpSender string
param enablePublicRecovery bool
param previousCertificates array

var identities = union({ '${registryPullIdentityId}': {} }, {})
var registry = [{ server: registryServer, identity: registryPullIdentityId }]
var previousPfxNames = [for certificate in previousCertificates: certificate.secretName]
var previousPasswordNames = [for certificate in previousCertificates: certificate.passwordSecretName]
var sharedSecretNames = concat(['tenant-proof', 'protection-pfx', 'protection-password', 'smtp-password'], previousPfxNames, previousPasswordNames)
var sharedSecrets = [for name in sharedSecretNames: {
  name: name
  keyVaultUrl: '${vaultUri}secrets/${name}'
  identity: 'system'
}]
var baseEnv = [
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
  { name: 'PublicOrigin', value: publicOrigin }
  { name: 'AllowedHosts', value: publicHost }
  { name: 'Storage__Provider', value: 'Azure' }
  { name: 'Storage__ContainerUri', value: containerUri }
  { name: 'Storage__InstallationId', value: installationId }
  { name: 'Deployment__Replicas', value: string(maxReplicas) }
  { name: 'TenantContext__ProofKeyFile', value: '/secrets/tenant-proof' }
  { name: 'DataProtection__CertificatePath', value: '/secrets/protection-pfx' }
  { name: 'DataProtection__CertificateFormat', value: 'Base64' }
  { name: 'DataProtection__CertificatePasswordFile', value: '/secrets/protection-password' }
  { name: 'Identity__DeliveryProvider', value: 'Smtp' }
  { name: 'Identity__PublicRecoveryEnabled', value: string(enablePublicRecovery) }
  { name: 'Identity__PublicInvitationEnabled', value: 'false' }
  { name: 'Smtp__Host', value: smtpHost }
  { name: 'Smtp__Port', value: string(smtpPort) }
  { name: 'Smtp__Security', value: 'StartTls' }
  { name: 'Smtp__Username', value: smtpUsername }
  { name: 'Smtp__Sender', value: smtpSender }
  { name: 'Smtp__PasswordFile', value: '/secrets/smtp-password' }
]
var previousPaths = [for (certificate, i) in previousCertificates: { name: 'DataProtection__PreviousCertificates__${i}__Path', value: '/secrets/${certificate.secretName}' }]
var previousFormats = [for (certificate, i) in previousCertificates: { name: 'DataProtection__PreviousCertificates__${i}__Format', value: 'Base64' }]
var previousPasswords = [for (certificate, i) in previousCertificates: { name: 'DataProtection__PreviousCertificates__${i}__PasswordFile', value: '/secrets/${certificate.passwordSecretName}' }]
var sharedEnv = concat(baseEnv, previousPaths, previousFormats, previousPasswords)
var proxyAddresses = [for (address, i) in trustedProxyAddresses: { name: 'ReverseProxy__KnownProxies__${i}', value: address }]
var proxyNetworks = [for (network, i) in trustedProxyNetworks: { name: 'ReverseProxy__KnownNetworks__${i}', value: network }]
var proxyEnv = concat(
  [{ name: 'ReverseProxy__ForwardLimit', value: '1' }],
  proxyAddresses,
  proxyNetworks
)
var secretFiles = [for name in sharedSecretNames: { secretRef: name, path: name }]
var volume = [{
  name: 'secrets'
  storageType: 'Secret'
  secrets: secretFiles
}]
var mounts = [{ volumeName: 'secrets', mountPath: '/secrets' }]
// This is not a credential: SqlClient obtains a token for the workload's system identity.
var connection = 'Server=tcp:${sqlHost},1433;Database=${databaseName};Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Max Pool Size=20;Connect Timeout=15'
resource web 'Microsoft.App/containerApps@2025-01-01' = {
  name: '${prefix}-web'
  location: location
  identity: { type: 'SystemAssigned, UserAssigned', userAssignedIdentities: identities }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Multiple'
      maxInactiveRevisions: 5
      registries: registry
      secrets: activate ? sharedSecrets : []
      ingress: activate ? {
        external: publishIngress
        targetPort: 8080
        allowInsecure: false
        transport: 'http'
        customDomains: empty(customDomainCertificateId) ? [] : [{ name: publicHost, bindingType: 'SniEnabled', certificateId: customDomainCertificateId }]
        stickySessions: { affinity: 'none' }
        // Pin the serving revision on upgrades; never implicitly promote the latest revision.
        traffic: releaseTraffic
      } : null
    }
    template: {
      terminationGracePeriodSeconds: 60
      containers: [{
        name: 'web'
        image: image
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: activate ? concat(sharedEnv, proxyEnv, [{ name: 'ConnectionStrings__Workbench', value: connection }]) : []
        volumeMounts: activate ? mounts : []
        probes: activate ? [
          {
            type: 'Startup'
            httpGet: { path: '/health/live', port: 8080, httpHeaders: [{ name: 'Host', value: publicHost }] }
            periodSeconds: 5
            timeoutSeconds: 2
            failureThreshold: 24
          }
          {
            type: 'Liveness'
            httpGet: { path: '/health/live', port: 8080, httpHeaders: [{ name: 'Host', value: publicHost }] }
            periodSeconds: 10
            timeoutSeconds: 2
            failureThreshold: 3
          }
          {
            type: 'Readiness'
            httpGet: { path: '/health/ready', port: 8080, httpHeaders: [{ name: 'Host', value: publicHost }] }
            periodSeconds: 10
            timeoutSeconds: 10
            failureThreshold: 3
          }
        ] : []
      }]
      volumes: activate ? volume : []
      scale: {
        minReplicas: 0
        maxReplicas: maxReplicas
        rules: activate ? [{ name: 'http', http: { metadata: { concurrentRequests: '20' } } }] : []
      }
    }
  }
}
resource worker 'Microsoft.App/jobs@2025-01-01' = {
  name: '${prefix}-worker'
  location: location
  identity: { type: 'SystemAssigned, UserAssigned', userAssignedIdentities: identities }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: activate && workerEnabled ? 'Schedule' : 'Manual'
      replicaTimeout: 90
      replicaRetryLimit: 1
      manualTriggerConfig: activate && workerEnabled ? null : { parallelism: 1, replicaCompletionCount: 1 }
      scheduleTriggerConfig: activate && workerEnabled ? { cronExpression: workerSchedule, parallelism: 1, replicaCompletionCount: 1 } : null
      registries: registry
      secrets: activate ? sharedSecrets : []
    }
    template: {
      containers: [{
        name: 'worker'
        image: image
        command: ['dotnet', 'Workbench.Server.dll']
        args: ['--worker', '--drain']
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: activate ? concat(sharedEnv, [
          { name: 'ConnectionStrings__Worker', value: connection }
          { name: 'Worker__MaxItems', value: '100' }
          { name: 'Worker__MaxDurationSeconds', value: '45' }
        ]) : []
        volumeMounts: activate ? mounts : []
      }]
      volumes: activate ? volume : []
    }
  }
}
resource migration 'Microsoft.App/jobs@2025-01-01' = {
  name: '${prefix}-migration'
  location: location
  identity: { type: 'SystemAssigned, UserAssigned', userAssignedIdentities: identities }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 1800
      replicaRetryLimit: 0
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: registry
      secrets: activate ? [{ name: 'migration-connection', keyVaultUrl: '${vaultUri}secrets/migration-connection', identity: 'system' }] : []
    }
    template: {
      containers: [{
        name: 'migration'
        image: image
        command: ['dotnet', '/opt/workbench/database/Workbench.Database.dll']
        args: ['migrate', '--connection-file', '/secrets/connection', '--expected-database', databaseName]
        resources: { cpu: json('0.5'), memory: '1Gi' }
        volumeMounts: activate ? mounts : []
      }]
      volumes: activate ? [{ name: 'secrets', storageType: 'Secret', secrets: [{ secretRef: 'migration-connection', path: 'connection' }] }] : []
    }
  }
}
output webPrincipalId string = web.identity.principalId
output workerPrincipalId string = worker.identity.principalId
output migrationPrincipalId string = migration.identity.principalId
output webId string = web.id
output workerId string = worker.id
output migrationId string = migration.id
