# HelpDesk en Azure

Plantillas Bicep para publicar HelpDesk con una URL pública, usando Container Apps, Azure SQL, Key Vault y managed identities.

> **Estado:** estas plantillas compilan, y CI lo comprueba en cada push. **Todavía no se han desplegado en una suscripción real**, así que el primer despliegue puede pedir ajustes. Revisa primero con `what-if`.

## Qué crea

| Recurso | Para qué | Notas |
|---|---|---|
| Container Apps: `api`, `bff`, `admin`, `keycloak` | La aplicación | Solo `bff`, `admin` y `keycloak` se publican en internet. Todas bajan a cero instancias sin tráfico |
| Container Apps Job: `migrator` | Aplicar las migraciones | Se lanza a mano o desde el pipeline |
| Azure SQL: bases `HelpDesk` y `Keycloak` | Los datos | Oferta gratuita, serverless |
| Key Vault | Los secretos | Se leen con una managed identity |
| Dos managed identities | Quién entra a SQL y a Key Vault | `app` lee y escribe datos, y `migrator` puede cambiar el esquema |
| Azure Files | Las llaves de Data Protection | Sin esto, cada arranque invalida las sesiones |
| Log Analytics | Los logs | Retención de 30 días |

Redis no está: la API funciona sin él. Por eso cada aplicación queda limitada a una instancia. Para escalar hay que agregar Redis y sacar de la memoria del panel los tokens de sesión.

## Costo

Se paga solo lo que supere los montos gratuitos. **Confírmalo en la [calculadora de precios](https://azure.microsoft.com/pricing/calculator/) antes de desplegar y crea una alerta de presupuesto.** Los montos son mensuales:

- **Container Apps:** [180.000 vCPU-segundos, 360.000 GiB-segundos y 2 millones de peticiones gratis](https://learn.microsoft.com/azure/container-apps/billing) por suscripción. Con todo encendido son 2,5 vCPU, así que alcanzan para unas 20 horas de uso.
- **Azure SQL:** [100.000 vCore-segundos y 32 GB gratis por base](https://learn.microsoft.com/azure/azure-sql/database/free-tier). Al agotarlos, la base se pausa hasta el mes siguiente y no cobra. Con 0,5 vCore son unas 55 horas.
- **Key Vault, Azure Files y Log Analytics:** centavos con este uso, pero no son gratis.

**Ojo con la base:** una conexión abierta [impide que se pause y sigue consumiendo](https://learn.microsoft.com/azure/azure-sql/database/free-tier). Por eso la API tiene que poder bajar a cero: si la dejas siempre encendida, el monitor de SLA y los health checks mantienen la base despierta, y lo gratuito se acaba en unos días. El costo de eso es que, sin tráfico, nadie vigila los plazos de SLA.

## Desplegar

Hace falta la [CLI de Azure](https://learn.microsoft.com/cli/azure/install-azure-cli) y que CI ya haya publicado las imágenes en GHCR.

```bash
az login
az group create --name helpdesk --location eastus2

# Los secretos, solo en esta terminal. Guárdalos en tu gestor de contraseñas: los necesitas para volver a desplegar.
export JWT_KEY=$(openssl rand -base64 48)
export BFF_SECRET=$(openssl rand -hex 32)
export ADMIN_SECRET=$(openssl rand -hex 32)
export KEYCLOAK_ADMIN=$(openssl rand -hex 16)
export DEMO_PASSWORD=$(openssl rand -hex 16)
export KEYCLOAK_DB="Aa1-$(openssl rand -hex 16)"

# El commit debe tener sus imágenes publicadas: el último de main que pasó CI.
export IMAGE_TAG="sha-$(git rev-parse origin/main)"

parameters=(
  imageTag="$IMAGE_TAG"
  sqlAdminObjectId="$(az ad signed-in-user show --query id -o tsv)"
  sqlAdminLogin="$(az ad signed-in-user show --query userPrincipalName -o tsv)"
  jwtSigningKey="$JWT_KEY" bffClientSecret="$BFF_SECRET" adminClientSecret="$ADMIN_SECRET"
  keycloakAdminPassword="$KEYCLOAK_ADMIN" demoUsersPassword="$DEMO_PASSWORD" keycloakDatabasePassword="$KEYCLOAK_DB"
)

# Primero, qué va a cambiar. No crea nada.
az deployment group what-if --resource-group helpdesk --template-file deploy/azure/main.bicep --parameters "${parameters[@]}"

az deployment group create --resource-group helpdesk --template-file deploy/azure/main.bicep --parameters "${parameters[@]}" --query properties.outputs
```

Una sola vez, dale permisos a las identidades en la base. Tienes que ser el administrador de Entra del servidor, y tu IP debe estar permitida en el firewall de SQL:

```bash
sqlcmd -S <sqlServer de la salida> -d HelpDesk -G -I -i deploy/azure/grant-identities.sql
```

Y aplica las migraciones:

```bash
az containerapp job start --name helpdesk-migrator --resource-group helpdesk
```

El portal y el panel quedan en `portalUrl` y `panelUrl`, en la salida del despliegue. La primera visita tarda: Keycloak y la base arrancan desde cero, y puede tomar un par de minutos.

**Ojo:** los usuarios de demostración del realm (`camila`, `pedro`, `laura`) quedan en internet con `DEMO_PASSWORD`, y el realm permite que cualquiera se registre. Sirve para una demostración, no para datos reales.

## Pipeline: desplegar en cada push

CI ya tiene el job `deploy`. Se activa cuando el repositorio tiene estas variables, y entra a Azure con OIDC, sin guardar una contraseña en GitHub.

```bash
az ad app create --display-name helpdesk-github
export APP_ID=$(az ad app list --display-name helpdesk-github --query '[0].appId' -o tsv)
az ad sp create --id "$APP_ID"

# Solo este repositorio, y solo desde el ambiente production, puede entrar como esta aplicación.
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "github-production",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:jcampos1981/T_Training_Net_FullStack:environment:production",
  "audiences": ["api://AzureADTokenExchange"]
}'

az role assignment create --assignee "$APP_ID" --role Contributor \
  --scope "$(az group show --name helpdesk --query id -o tsv)"

gh variable set AZURE_CLIENT_ID --body "$APP_ID"
gh variable set AZURE_TENANT_ID --body "$(az account show --query tenantId -o tsv)"
gh variable set AZURE_SUBSCRIPTION_ID --body "$(az account show --query id -o tsv)"
gh variable set AZURE_RESOURCE_GROUP --body helpdesk
```

El job actualiza las imágenes. Si cambias las plantillas, vuelve a correr `az deployment group create` a mano.

## Borrar todo

```bash
az group delete --name helpdesk
```

Key Vault queda 90 días en "eliminación suave" y su nombre sigue ocupado. Con el mismo grupo de recursos, ese nombre se repite: al volver a desplegar hay que recuperarlo o purgarlo antes (`az keyvault purge`).

## Lo que falta para producción de verdad

- Una red privada para SQL, en vez de permitir cualquier servicio de Azure.
- Cerrar el registro abierto del realm (`registrationAllowed`) y quitar los usuarios de demostración, o usar un proveedor administrado como Microsoft Entra External ID.
- Dominio propio, y envolver las llaves de Data Protection con Key Vault.
- SQL con un nivel de pago y con SLA: la oferta gratuita no lo incluye.
