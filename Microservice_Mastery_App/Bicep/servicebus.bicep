param location string = resourceGroup().location
@allowed([
  'dev'
  'qa'
  'staging'
  'prod'
])
param environment string = 'dev'
var namespaceName = 'orderflow-${environment}-sb-${uniqueString(resourceGroup().id)}'
var skuByEnvironment = {
  dev: 'Basic'
  qa: 'Basic'
  staging: 'Standard'
  prod: 'Standard'
}
var skuName = skuByEnvironment[environment] 

var queueName = 'orderflow-orders'

resource sbNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: namespaceName
  location: location
  sku: {
    name: skuName
  }
}

resource ordersQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: sbNamespace
  name: queueName
  properties: {
    maxDeliveryCount: 10
    lockDuration: 'PT30S'
  }
}