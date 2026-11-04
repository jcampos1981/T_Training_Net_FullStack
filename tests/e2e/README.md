# Pruebas de punta a punta

Prueban lo que las pruebas de integración de `tests/HelpDesk.Api.IntegrationTests` no pueden: Edge de verdad, Keycloak, Redis, varias instancias de la API y servicios que se caen.

| | Pruebas de integración | Estas |
|---|---|---|
| **Corren en** | Memoria, con SQL Server en un contenedor propio | Los procesos reales y `deploy/compose.yaml` |
| **Tardan** | Unos 2 minutos | De 1 a 8 minutos por suite |
| **Dependen de la máquina** | Solo de Docker | De los User Secrets, `deploy/.env` y Edge |
| **Se corren** | En cada cambio y en CI | Antes de publicar un cambio grande |

## Cómo correrlas

Hace falta Docker con `docker compose -f deploy/compose.yaml up -d`, los usuarios de desarrollo sembrados y Edge instalado.

```powershell
powershell -File tests/e2e/run.ps1                        # todas, menos la espera del token real
powershell -File tests/e2e/run.ps1 -Suite health,cache    # solo esas
powershell -File tests/e2e/run.ps1 -Long                  # además espera que venza el token de Keycloak (5 min por suite)
powershell -File tests/e2e/run.ps1 -Suite containers -NoBuild    # la aplicación en contenedores; no entra en "todas"
powershell -File tests/e2e/run.ps1 -Suite kubernetes -NoBuild    # en el clúster de deploy/k8s, ya instalada; tampoco
```

Las capturas y la salida de cada suite quedan en `tests/e2e/shots/`, que git ignora.

**Ojo:** detienen de verdad SQL Server, Redis y Keycloak, y detienen los procesos `HelpDesk.*` que estén corriendo. No las corras si estás usando esos servicios.

## Las suites

| Suite | Qué prueba |
|---|---|
| `containers` | Las imágenes de producción con `deploy/compose.app.yaml`: usuario no root, sin shell, login real, un aviso en vivo entre aplicaciones, y que la sesión sobreviva a recrear el contenedor |
| `kubernetes` | Varias réplicas en un clúster: avisos entre pods, sesiones al matar pods, actualización sin cortes, rollback, readiness sin base de datos, y autoescalado con carga |
| `api-keycloak`, `api-portal`, `api-bff`, `api-cors` | La API por HTTP, con tokens reales de Keycloak |
| `hub` | SignalR, con un token de 1 minuto: se cierra la conexión al vencer |
| `bff-outage` | Keycloak detenido: las sesiones siguen vivas |
| `sla` | Monitor de SLA: backlog, dos instancias compitiendo, SQL Server detenido, y el panel |
| `cache` | HybridCache, output caching y Redis entre dos instancias |
| `redis-clients` | Redis caído: el portal y el panel no entran en bucle y se recuperan solos |
| `health` | Health checks, apagando cada servicio |
| `admin-b4`, `admin-b5`, `admin-live` | El panel Blazor en Edge |
| `portal-b3`, `portal-live`, `portal-vite` | El portal React en Edge, por el BFF y por Vite |
| `portal-b2`, `portal-b3-vite` | El portal por Vite en el puerto 5173. Si está ocupado se saltan, porque Keycloak solo acepta ese puerto |
