// HelpDesk en Azure: Container Apps, Azure SQL, Key Vault y managed identities. Instrucciones en README.md.
//
// Las aplicaciones no tienen contraseña de base de datos: se identifican con una managed identity. Sus demás secretos están en Key
// Vault, y Container Apps los lee con esa misma identidad. Keycloak es la excepción: su driver necesita usuario y contraseña.

@description('Región de Azure.')
param location string = resourceGroup().location

@description('Prefijo de los nombres. Key Vault, SQL y el almacenamiento se llaman así más un sufijo, y deben ser únicos en todo Azure.')
@minLength(3)
@maxLength(10)
param prefix string = 'helpdesk'

@description('Dueño de las imágenes en GHCR.')
param imageOwner string = 'jcampos1981'

@description('Tag de las imágenes: sha-<commit completo>. No latest: así se sabe exactamente qué código corre.')
param imageTag string

@description('objectId en Entra de quien administra SQL (az ad signed-in-user show --query id -o tsv).')
param sqlAdminObjectId string

@description('Nombre en Entra de quien administra SQL.')
param sqlAdminLogin string

@secure()
@description('Base64 de al menos 32 bytes: con esta llave firma la API sus JWT.')
param jwtSigningKey string

@secure()
param bffClientSecret string

@secure()
param adminClientSecret string

@secure()
param keycloakAdminPassword string

@secure()
@description('Contraseña de los usuarios de demostración del realm.')
param demoUsersPassword string

@secure()
@description('Contraseña con que Keycloak entra a su propia base.')
param keycloakDatabasePassword string

var suffix = uniqueString(resourceGroup().id)
var keycloakDatabaseLogin = 'keycloak'

// Rol integrado: leer el valor de los secretos, y nada más.
var keyVaultSecretsUserRole = '4633458b-17de-408a-b874-0445c86b69e6'

// ---- Identidades: una para atender, otra para cambiar el esquema ----

resource appIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-app'
  location: location
}

resource migratorIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-migrator'
  location: location
}

// ---- Secretos ----

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${prefix}-${suffix}'
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
  }
}

resource jwtSigningKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'jwt-signing-key'
  properties: { value: jwtSigningKey }
}

resource bffClientSecretSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'bff-client-secret'
  properties: { value: bffClientSecret }
}

resource adminClientSecretSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'admin-client-secret'
  properties: { value: adminClientSecret }
}

resource keycloakAdminPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'keycloak-admin-password'
  properties: { value: keycloakAdminPassword }
}

resource demoUsersPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'demo-users-password'
  properties: { value: demoUsersPassword }
}

resource keycloakDatabasePasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'keycloak-database-password'
  properties: { value: keycloakDatabasePassword }
}

resource appReadsSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, appIdentity.id, keyVaultSecretsUserRole)
  properties: {
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRole)
  }
}

// ---- Base de datos ----

resource sql 'Microsoft.Sql/servers@2023-08-01' = {
  name: '${prefix}-${suffix}'
  location: location
  properties: {
    // Solo Keycloak entra con usuario y contraseña. Las aplicaciones lo hacen con su identidad.
    administratorLogin: keycloakDatabaseLogin
    administratorLoginPassword: keycloakDatabasePassword
    minimalTlsVersion: '1.2'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'User'
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: false
    }
  }
}

// Container Apps en consumo no tiene una IP fija: este es el modo "cualquier servicio de Azure". En producción real, una red privada.
resource sqlAllowsAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  parent: sql
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// La oferta gratuita de Azure SQL: serverless, y al agotar lo gratuito del mes se pausa en vez de cobrar.
var freeDatabase = {
  sku: {
    name: 'GP_S_Gen5_2'
    tier: 'GeneralPurpose'
  }
  properties: {
    useFreeLimit: true
    freeLimitExhaustionBehavior: 'AutoPause'
    autoPauseDelay: 60
    minCapacity: json('0.5')
  }
}

resource helpDeskDatabase 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sql
  name: 'HelpDesk'
  location: location
  sku: freeDatabase.sku
  properties: freeDatabase.properties
}

resource keycloakDatabase 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sql
  name: 'Keycloak'
  location: location
  sku: freeDatabase.sku
  properties: freeDatabase.properties
}

// ---- Ambiente de Container Apps ----

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-logs'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: '${prefix}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

// ---- Las llaves de Data Protection: sin esto, cada arranque invalida las sesiones ----

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: take('${prefix}${suffix}', 24)
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

resource files 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource keyShares 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = [for app in ['api', 'bff', 'admin']: {
  parent: files
  name: '${app}-keys'
}]

resource keyStorages 'Microsoft.App/managedEnvironments/storages@2025-01-01' = [for (app, index) in ['api', 'bff', 'admin']: {
  parent: environment
  name: '${app}-keys'
  properties: {
    azureFile: {
      accountName: storage.name
      accountKey: storage.listKeys().keys[0].value
      shareName: keyShares[index].name
      accessMode: 'ReadWrite'
    }
  }
}]

// ---- Direcciones ----

var keycloakName = '${prefix}-keycloak'
var bffName = '${prefix}-bff'
var adminName = '${prefix}-admin'
var keycloakUrl = 'https://${keycloakName}.${environment.properties.defaultDomain}'
var realmUrl = '${keycloakUrl}/realms/helpdesk'
var portalUrl = 'https://${bffName}.${environment.properties.defaultDomain}'
var panelUrl = 'https://${adminName}.${environment.properties.defaultDomain}'

var helpDeskConnection = 'Server=tcp:${sql.properties.fullyQualifiedDomainName},1433;Database=${helpDeskDatabase.name};Authentication=Active Directory Managed Identity;Encrypt=True'

var image = {
  api: 'ghcr.io/${imageOwner}/helpdesk-api:${imageTag}'
  migrator: 'ghcr.io/${imageOwner}/helpdesk-migrator:${imageTag}'
  bff: 'ghcr.io/${imageOwner}/helpdesk-bff:${imageTag}'
  admin: 'ghcr.io/${imageOwner}/helpdesk-admin:${imageTag}'
}

// Lo que vale igual para el BFF y el panel: están detrás del ingress de Container Apps, que termina el TLS y avisa con
// X-Forwarded-Proto. Esta variable le dice a ASP.NET Core que confíe en ese header, sin importar la IP del proxy.
var behindProxy = [
  { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
  { name: 'DataProtection__KeysDirectory', value: '/keys' }
  { name: 'Keycloak__Authority', value: realmUrl }
]

// ---- Keycloak ----

// El realm se monta como archivo desde un secreto. Lleva ${...} en vez de contraseñas: Keycloak los reemplaza con sus variables.
resource keycloak 'Microsoft.App/containerApps@2025-01-01' = {
  name: keycloakName
  location: location
  dependsOn: [keycloakDatabase, sqlAllowsAzure]
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${appIdentity.id}': {}
    }
  }
  properties: {
    environmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
      }
      secrets: [
        { name: 'realm', value: loadTextContent('../keycloak/helpdesk-realm.json') }
        { name: 'admin-password', keyVaultUrl: keycloakAdminPasswordSecret.properties.secretUri, identity: appIdentity.id }
        { name: 'demo-password', keyVaultUrl: demoUsersPasswordSecret.properties.secretUri, identity: appIdentity.id }
        { name: 'database-password', keyVaultUrl: keycloakDatabasePasswordSecret.properties.secretUri, identity: appIdentity.id }
        { name: 'bff-client-secret', keyVaultUrl: bffClientSecretSecret.properties.secretUri, identity: appIdentity.id }
        { name: 'admin-client-secret', keyVaultUrl: adminClientSecretSecret.properties.secretUri, identity: appIdentity.id }
      ]
    }
    template: {
      containers: [
        {
          name: 'keycloak'
          image: 'quay.io/keycloak/keycloak:26.8.1'
          args: ['start', '--import-realm']
          resources: {
            cpu: json('1')
            memory: '2Gi'
          }
          env: [
            { name: 'KC_DB', value: 'mssql' }
            { name: 'KC_DB_URL', value: 'jdbc:sqlserver://${sql.properties.fullyQualifiedDomainName}:1433;databaseName=${keycloakDatabase.name};encrypt=true' }
            { name: 'KC_DB_USERNAME', value: keycloakDatabaseLogin }
            { name: 'KC_DB_PASSWORD', secretRef: 'database-password' }
            { name: 'KC_TRANSACTION_XA_ENABLED', value: 'false' }
            { name: 'KC_HOSTNAME', value: keycloakUrl }
            { name: 'KC_HTTP_ENABLED', value: 'true' }
            { name: 'KC_PROXY_HEADERS', value: 'xforwarded' }
            { name: 'KC_HEALTH_ENABLED', value: 'true' }
            { name: 'KC_BOOTSTRAP_ADMIN_USERNAME', value: 'admin' }
            { name: 'KC_BOOTSTRAP_ADMIN_PASSWORD', secretRef: 'admin-password' }
            { name: 'HELPDESK_DEV_PASSWORD', secretRef: 'demo-password' }
            { name: 'HELPDESK_BFF_CLIENT_SECRET', secretRef: 'bff-client-secret' }
            { name: 'HELPDESK_ADMIN_CLIENT_SECRET', secretRef: 'admin-client-secret' }
            { name: 'HELPDESK_BFF_PUBLIC_URL', value: portalUrl }
            { name: 'HELPDESK_ADMIN_PUBLIC_URL', value: panelUrl }
          ]
          volumeMounts: [
            {
              volumeName: 'realm'
              mountPath: '/opt/keycloak/data/import'
            }
          ]
        }
      ]
      volumes: [
        {
          name: 'realm'
          storageType: 'Secret'
          secrets: [
            {
              secretRef: 'realm'
              path: 'helpdesk-realm.json'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
}

// ---- Migraciones: un job que se lanza a mano o desde el pipeline, con la identidad que sí puede cambiar el esquema ----

resource migrator 'Microsoft.App/jobs@2025-01-01' = {
  name: '${prefix}-migrator'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${migratorIdentity.id}': {}
    }
  }
  properties: {
    environmentId: environment.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 600
      replicaRetryLimit: 1
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
    }
    template: {
      containers: [
        {
          name: 'migrator'
          image: image.migrator
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'ConnectionStrings__HelpDesk', value: '${helpDeskConnection};User Id=${migratorIdentity.properties.clientId}' }
          ]
        }
      ]
    }
  }
}

// ---- Las aplicaciones ----

module api 'app.bicep' = {
  name: 'api'
  dependsOn: [appReadsSecrets, sqlAllowsAzure, keyStorages]
  params: {
    name: '${prefix}-api'
    location: location
    environmentId: environment.id
    identityId: appIdentity.id
    image: image.api
    external: false
    keysStorageName: 'api-keys'
    secrets: [
      { name: 'jwt-signing-key', keyVaultUrl: jwtSigningKeySecret.properties.secretUri }
    ]
    env: [
      { name: 'ConnectionStrings__HelpDesk', value: '${helpDeskConnection};User Id=${appIdentity.properties.clientId}' }
      { name: 'Jwt__SigningKey', secretRef: 'jwt-signing-key' }
      { name: 'Keycloak__Authority', value: realmUrl }
      { name: 'DataProtection__KeysDirectory', value: '/keys' }
    ]
  }
}

module bff 'app.bicep' = {
  name: 'bff'
  dependsOn: [appReadsSecrets, keyStorages]
  params: {
    name: bffName
    location: location
    environmentId: environment.id
    identityId: appIdentity.id
    image: image.bff
    external: true
    keysStorageName: 'bff-keys'
    secrets: [
      { name: 'client-secret', keyVaultUrl: bffClientSecretSecret.properties.secretUri }
    ]
    env: concat(behindProxy, [
      { name: 'Keycloak__ClientSecret', secretRef: 'client-secret' }
      { name: 'ReverseProxy__Clusters__helpdesk-api__Destinations__api__Address', value: 'https://${api.outputs.fqdn}/' }
    ])
  }
}

module admin 'app.bicep' = {
  name: 'admin'
  dependsOn: [appReadsSecrets, keyStorages]
  params: {
    name: adminName
    location: location
    environmentId: environment.id
    identityId: appIdentity.id
    image: image.admin
    external: true
    keysStorageName: 'admin-keys'
    secrets: [
      { name: 'client-secret', keyVaultUrl: adminClientSecretSecret.properties.secretUri }
    ]
    env: concat(behindProxy, [
      { name: 'Keycloak__ClientSecret', secretRef: 'client-secret' }
      { name: 'HelpDeskApi__BaseUrl', value: 'https://${api.outputs.fqdn}' }
    ])
  }
}

output portalUrl string = portalUrl
output panelUrl string = panelUrl
output keycloakUrl string = keycloakUrl
output sqlServer string = sql.properties.fullyQualifiedDomainName
output appIdentityName string = appIdentity.name
output migratorIdentityName string = migratorIdentity.name
output migratorJobName string = migrator.name
