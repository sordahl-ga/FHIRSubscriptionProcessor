@description('Prefix for all resources')
param prefix string = 'fsp'

@description('Enable R5 Backport Subscription Support')
param enableR5Backport bool = false

@description('Location for all resources.')
param location string = resourceGroup().location

@allowed([ 'FhirService', 'APIforFhir', 'FhirServer' ])
@description('Type of FHIR instance to integrate the subscription processor with.')
param fhirType string = 'FhirService'

@description('Name of the FHIR Service. Format is "workspace/fhirService".')
param fhirServiceName string = ''

@description('Name of the API for FHIR')
param apiForFhirName string = ''

@description('The full URL of the OSS FHIR Server')
param fhirServerUrl string = ''

@allowed([ 'managedIdentity', 'servicePrincipal' ])
@description('Type of FHIR authentication.')
param authenticationType string = 'managedIdentity'

@allowed([ 'B1', 'B2', 'B3', 'S1', 'S2', 'S3', 'P1v2', 'P2v2', 'P3v2', 'P1v3', 'P2v3', 'P3v3' ])
@description('Size of the app service to run loader function')
param appServiceSize string = 'B1'

@description('If not using MSI, client ID of the service account used to connect to the FHIR Server')
param serviceAccountClientId string = ''

@description('If not using MSI, client secret of the service account used to connect to the FHIR Server')
@secure()
param serviceAccountSecret string = ''

@description('Audience used for FHIR Server tokens. Leave blank to use the FHIR url which will work for default FHIR deployments.')
param fhirAudience string = ''

@description('Automatically create a role assignment for the function app to access the FHIR service.')
param createRoleAssignment bool = true

@description('The FHIR Subscription processor function app needs to access the FHIR service. This is the role assignment ID to use.')
param fhirContributorRoleAssignmentId string = '5a1fc7df-4bf1-4951-a576-89034ee01acd'


var repoUrl = 'https://github.com/sordahl-ga/FHIRSubscriptionProcessor'

var fhirUrl = fhirType == 'FhirService' ? 'https://${replace(fhirServiceName, '/', '-')}.fhir.azurehealthcareapis.com' : fhirType == 'APIforFhir' ? 'https://${apiForFhirName}.azurehealthcareapis.com' : fhirServerUrl

@description('Tenant ID where resources are deployed')
var tenantId = subscription().tenantId

@description('Tags for all Azure resources in the solution')
var appTags = {
  AppID: 'fhir-subscriptionprocessor-function'
}

var uniqueResourceIdentifier = substring(uniqueString(resourceGroup().id, prefix), 0, 4)
var prefixNameClean = '${replace(prefix, '-', '')}${uniqueResourceIdentifier}'
var prefixNameCleanShort = length(prefixNameClean) > 16 ? substring(prefixNameClean, 0, 8) : prefixNameClean
@description('ServiceBus used for subscription processor')
resource servicebus 'Microsoft.ServiceBus/namespaces@2021-06-01-preview' = {
  name: '${prefixNameCleanShort}sb'
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
}
resource sbtopic 'Microsoft.ServiceBus/namespaces/topics@2021-06-01-preview' = {
  name: 'notifyfhirsub'
  parent: servicebus
  properties: {
    defaultMessageTimeToLive: 'P6M' //ISO 8601
    status: 'Active'
  }
}
resource sbsub 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2021-06-01-preview' = {
  name: 'channelnotify'
  parent: sbtopic
  properties: {
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P6M'
    lockDuration: 'PT5M'
    maxDeliveryCount: 20
    status: 'Active'
  }
}
@description('Redis Cache used for subscription processor')
resource redisCache 'Microsoft.Cache/Redis@2020-06-01' = {
  name: '${prefixNameCleanShort}redis'
  location: location
  properties: {
    enableNonSslPort: false
    minimumTlsVersion: '1.2'
    sku: {
      capacity: 1
      family: 'C'
      name: 'Standard'
    }
  }
}

@description('Storage account used for function app')
resource storageAccount 'Microsoft.Storage/storageAccounts@2021-08-01' = {
  name: '${prefixNameCleanShort}stor'
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'

  resource service 'blobServices' = {
    name: 'default'
  }
}
resource queueServices 'Microsoft.Storage/storageAccounts/queueServices@2022-09-01' = {
  name: 'default'
  parent: storageAccount
}
@description('Storage Queue for FHIR Subscription processing')
resource storageQueueFhirSub 'Microsoft.Storage/storageAccounts/queueServices/queues@2022-09-01' = {
  name: 'fhirsubprocessorqueue'
  parent: queueServices
  properties: {
    metadata: {}
  }
}

@description('App Service used to run Azure Function')
resource hostingPlan 'Microsoft.Web/serverfarms@2021-03-01' = {
  name: '${prefixNameCleanShort}-app'
  location: location
  kind: 'functionapp'
  sku: {
    name: appServiceSize
  }

  properties: {
    targetWorkerCount: 2
  }
  tags: appTags
}

@description('Azure Function used to run subscription processor')
resource functionApp 'Microsoft.Web/sites@2021-03-01' = {
  name: '${prefixNameCleanShort}-func'
  location: location
  kind: 'functionapp'

  identity: {
    type: 'SystemAssigned'
  }

  properties: {
    httpsOnly: true
    enabled: true
    serverFarmId: hostingPlan.id
    clientAffinityEnabled: false
    siteConfig: {
      alwaysOn: true
      appSettings: [
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
      ]
    }
  }

  dependsOn: [
    storageAccount
  ]

  tags: appTags

  resource config 'config' = {
    name: 'web'
    properties: {
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
    }
  }

  resource ftpPublishingPolicy 'basicPublishingCredentialsPolicies' = {
    name: 'ftp'
    // Location is needed regardless of the warning.
    #disable-next-line BCP187
    location: location
    properties: {
      allow: false
    }
  }

  resource scmPublishingPolicy 'basicPublishingCredentialsPolicies' = {
    name: 'scm'
    // Location is needed regardless of the warning.
    #disable-next-line BCP187
    location: location
    properties: {
      allow: false
    }
  }
}

var sblistkeysep = '${servicebus.id}/AuthorizationRules/RootManageSharedAccessKey'
var sbendpoint = 'Endpoint=sb://${servicebus.name}.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=${listKeys(sblistkeysep, servicebus.apiVersion).primaryKey}'
var storageAccountConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${storageAccount.listKeys().keys[0].value}'
var cacheConnectionString='${prefixNameCleanShort}redis.redis.cache.windows.net,abortConnect=false,ssl=true,password=${redisCache.listKeys().primaryKey}'

resource fhirSubprocessorAppSettings 'Microsoft.Web/sites/config@2021-03-01' = {
  name: 'appsettings'
  parent: functionApp
  properties: {
    AzureWebJobsStorage: storageAccountConnectionString
    FUNCTIONS_EXTENSION_VERSION: '~4'
    FUNCTIONS_WORKER_RUNTIME: 'dotnet'
    APPINSIGHTS_INSTRUMENTATIONKEY: appInsights.properties.InstrumentationKey
    APPLICATIONINSIGHTS_CONNECTION_STRING: appInsights.properties.ConnectionString
    SCM_DO_BUILD_DURING_DEPLOYMENT: 'true'
	AzureFunctionsJobHost__functionTimeout: '01:00:00'
    'AzureWebJobs.SubscriptionEventHubProcessor.Disabled': '1'
    // Storage account to setup import from
    'FSP-STORAGEACCOUNT': storageAccountConnectionString
   // URL for the FHIR endpoint
    'FS-URL': fhirUrl
    // Resource for the FHIR endpoint.
    'FS-RESOURCE': empty(fhirAudience) ? fhirUrl : fhirAudience
    // Tenant of FHIR Server
    'FS-TENANT-NAME': tenantId
    'FS-ISMSI': authenticationType == 'managedIdentity' ? 'true' : 'false'
    'FS-CLIENT-ID': authenticationType == 'servicePrincipal' ? serviceAccountClientId : ''
    'FS-SECRET': authenticationType == 'servicePrincipal' ? serviceAccountSecret : ''
	'FSP-STORAGEQUEUENAME': 'fhirsubprocessorqueue'
	'FSP-REDISCONNECTION': cacheConnectionString
	'FSP-NOTIFYSB-CONNECTION': sbendpoint
	'FSP-NOTIFYSB-SUBSCRIPTION': sbsub.name
	'FSP-NOTIFYSB-TOPIC': sbtopic.name
    'FS-ISR5BACKPORT': enableR5Backport ? 'true' : 'false'
  }
}

@description('Uses source control deploy if requested')
resource functionAppDeployment 'Microsoft.Web/sites/sourcecontrols@2021-03-01' = {
  name: 'web'
  parent: functionApp
  properties: {
    repoUrl: repoUrl
    branch: 'master'
    isManualIntegration: true
  }
}


@description('Monitoring for Function App')
resource appInsights 'Microsoft.Insights/components@2020-02-02-preview' = {
  name: '${prefixNameCleanShort}-ai'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
  }
  tags: appTags
}

var fhirUrlClean = replace(split(fhirUrl, '.')[0], 'https://', '')
var fhirUrlCleanSplit = split(fhirUrlClean, '-')

resource fhirService 'Microsoft.HealthcareApis/workspaces/fhirservices@2021-06-01-preview' existing = if (fhirType == 'FhirService' && createRoleAssignment == true) {
  #disable-next-line prefer-interpolation
  name: concat(fhirUrlCleanSplit[0], '/', join(skip(fhirUrlCleanSplit, 1), '-'))
}

resource apiForFhir 'Microsoft.HealthcareApis/services@2021-11-01' existing = if (fhirType == 'APIforFhir' && createRoleAssignment == true) {
  name: fhirUrlClean
}

@description('Setup access between FHIR and the deployment script managed identity')
module functionFhirServiceRoleAssignment './roleAssignment.bicep' = if (fhirType == 'FhirService' && createRoleAssignment == true) {
  name: 'functionFhirServiceRoleAssignment'
  params: {
    resourceId: fhirService.id
    roleId: fhirContributorRoleAssignmentId
    principalId: functionApp.identity.principalId
  }
}

@description('Setup access between FHIR and the deployment script managed identity')
module functionApiForFhirRoleAssignment './roleAssignment.bicep' = if (fhirType == 'APIforFhir' && createRoleAssignment == true) {
  name: 'bulk-import-function-fhir-managed-id-role-assignment'
  params: {
    resourceId: apiForFhir.id
    roleId: fhirContributorRoleAssignmentId
    principalId: functionApp.identity.principalId
  }
}
