// Una aplicación de HelpDesk en Container Apps. Las tres se diferencian solo en lo que recibe este módulo.

param name string
param location string
param environmentId string
param identityId string
param image string

@description('true: se publica en internet. false: solo la alcanzan las otras aplicaciones del ambiente.')
param external bool

@description('Secretos de Key Vault, como { name, keyVaultUrl }. La aplicación los recibe sin que el valor pase por esta plantilla.')
param secrets array = []

@description('Variables de entorno, como { name, value } o { name, secretRef }.')
param env array

@description('Recurso compartido de Azure Files para las llaves de Data Protection. Vacío si no tiene.')
param keysStorageName string = ''

// Una sola instancia hasta tener Redis (avisos en vivo y caché compartidos) y los tokens del panel fuera de su memoria.
var maxReplicas = 1

var keysVolumeName = 'keys'
var hasKeys = !empty(keysStorageName)

resource app 'Microsoft.App/containerApps@2025-01-01' = {
  name: name
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    environmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: external
        targetPort: 8080
        transport: 'auto'
        // Blazor Server y SignalR mantienen estado en la instancia: si en el futuro hay varias, cada navegador sigue en la suya.
        stickySessions: {
          affinity: 'sticky'
        }
      }
      secrets: [for secret in secrets: {
        name: secret.name
        keyVaultUrl: secret.keyVaultUrl
        identity: identityId
      }]
    }
    template: {
      containers: [
        {
          name: name
          image: image
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: env
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/health/live'
                port: 8080
              }
              periodSeconds: 5
              failureThreshold: 30
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
              }
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
              }
            }
          ]
          volumeMounts: hasKeys
            ? [
                {
                  volumeName: keysVolumeName
                  mountPath: '/keys'
                }
              ]
            : []
        }
      ]
      volumes: hasKeys
        ? [
            {
              name: keysVolumeName
              storageType: 'AzureFile'
              storageName: keysStorageName
              // Azure Files no entiende de dueños: se monta a nombre del usuario no root de las imágenes chiseled.
              mountOptions: 'uid=1654,gid=1654'
            }
          ]
        : []
      scale: {
        minReplicas: 0 // sin tráfico no consume: el primer usuario espera unos segundos a que arranque
        maxReplicas: maxReplicas
      }
    }
  }
}

output fqdn string = app.properties.configuration.ingress.fqdn
