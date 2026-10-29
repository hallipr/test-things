targetScope = 'resourceGroup'

@description('Prefix for globally unique resource names. Deploy into the existing pahallis-sandbox resource group.')
@minLength(3)
@maxLength(15)
param namePrefix string = 'rundirectory'

param location string = resourceGroup().location

@description('Exact audience requested by the GitHub workflow; not a secret.')
param githubOidcAudience string = 'api://hallipr-test-things-directory-access'

@description('Existing user-assigned managed identity in this resource group.')
param managedIdentityName string = 'pahallis-sandbox-identity'

@description('Optional existing Application Insights component in this resource group.')
param applicationInsightsName string = ''

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' existing = if (!empty(applicationInsightsName)) {
  name: applicationInsightsName
}

resource brokerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: managedIdentityName
}

var suffix = uniqueString(subscription().subscriptionId, resourceGroup().id, namePrefix)
var hostStorageName = 'host${suffix}'
var runStorageName = 'runs${suffix}'
var functionName = '${namePrefix}-${suffix}'
var blobOwnerRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
var queueContributorRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
var storageContributorRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '17d1049b-9a84-46fb-8f53-869881c3d3ab')
var blobReaderRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
var blobDelegatorRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'db58b8e5-c6ad-4a2a-8342-4190687cbf4a')

resource runStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: runStorageName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    isHnsEnabled: true
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    publicNetworkAccess: 'Enabled'
    defaultToOAuthAuthentication: true
    networkAcls: { defaultAction: 'Allow', bypass: 'None' }
  }
}

resource runBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: runStorage
  name: 'default'
}

resource runs 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: runBlobService
  name: 'runs'
  properties: { publicAccess: 'None' }
}

resource hostStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: hostStorageName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    publicNetworkAccess: 'Enabled'
    defaultToOAuthAuthentication: true
    networkAcls: { defaultAction: 'Allow', bypass: 'None' }
  }
}

resource hostBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: hostStorage
  name: 'default'
}

resource deployments 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: hostBlobService
  name: 'deployments'
  properties: { publicAccess: 'None' }
}

// Flex Consumption avoids Azure Files connection strings and dedicated-plan idle cost.
resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${functionName}-plan'
  location: location
  kind: 'functionapp'
  sku: { name: 'FC1', tier: 'FlexConsumption' }
  properties: { reserved: true }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionName
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${brokerIdentity.id}': {} }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    functionAppConfig: {
      runtime: { name: 'dotnet-isolated', version: '10.0' }
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${hostStorage.properties.primaryEndpoints.blob}${deployments.name}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: brokerIdentity.id
          }
        }
      }
      scaleAndConcurrency: { maximumInstanceCount: 40, instanceMemoryMB: 2048 }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: hostStorage.name }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'AzureWebJobsStorage__clientId', value: brokerIdentity.properties.clientId }
        { name: 'AZURE_CLIENT_ID', value: brokerIdentity.properties.clientId }
        { name: 'GitHubOidcAudience', value: githubOidcAudience }
        { name: 'RunStorageAccountUrl', value: runStorage.properties.primaryEndpoints.dfs }
        ...(!empty(applicationInsightsName) ? [
          { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: applicationInsights!.properties.ConnectionString }
        ] : [])
      ]
    }
  }
}

resource runReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(runs.id, brokerIdentity.id, blobReaderRole)
  scope: runs
  properties: {
    roleDefinitionId: blobReaderRole
    principalId: brokerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource runDelegator 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(runStorage.id, brokerIdentity.id, blobDelegatorRole)
  scope: runStorage
  properties: {
    roleDefinitionId: blobDelegatorRole
    principalId: brokerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Host/runtime/deployment rights are confined to the separate host account.
resource hostBlobOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, brokerIdentity.id, blobOwnerRole)
  scope: hostStorage
  properties: {
    roleDefinitionId: blobOwnerRole
    principalId: brokerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource hostQueueContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, brokerIdentity.id, queueContributorRole)
  scope: hostStorage
  properties: {
    roleDefinitionId: queueContributorRole
    principalId: brokerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource hostAccountContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, brokerIdentity.id, storageContributorRole)
  scope: hostStorage
  properties: {
    roleDefinitionId: storageContributorRole
    principalId: brokerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output functionAppName string = functionApp.name
output brokerUrl string = 'https://${functionApp.properties.defaultHostName}/api/directory-access'
output runStorageAccountName string = runStorage.name
output hostStorageAccountName string = hostStorage.name
output functionPrincipalId string = brokerIdentity.properties.principalId
