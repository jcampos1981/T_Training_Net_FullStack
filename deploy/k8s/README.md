# HelpDesk en Kubernetes

La aplicación completa en un clúster local, con varias réplicas, probes, autoescalado y un chart de Helm.

| Carpeta | Qué hay |
|---|---|
| `deploy/k8s/cluster.yaml` | Un clúster k3s de un nodo, dentro de un contenedor |
| `deploy/k8s/infrastructure/` | SQL Server y Keycloak, con Kustomize |
| `deploy/k8s/redis-values.yaml` | Redis (Valkey), con un chart de terceros |
| `deploy/helm/helpdesk/` | La aplicación: nuestro chart |

Esto es para practicar y probar: el clúster corre `privileged`, SQL Server y Keycloak no tienen alta disponibilidad, y el certificado es autofirmado.

## Levantarlo

Solo hace falta Docker. `kubectl` viene con Docker Desktop, y Helm corre en un contenedor (`helm.ps1`).

```powershell
docker compose -f deploy/k8s/cluster.yaml up -d --wait

# Con las imágenes que publicó CI: el sha-<commit completo> de un commit de main que pasó el pipeline.
powershell -File deploy/k8s/install.ps1 -ImageTag "sha-$(git rev-parse origin/main)"

# O con las construidas en la máquina.
docker compose -f deploy/compose.yaml -f deploy/compose.app.yaml build migrator api bff admin
powershell -File deploy/k8s/install.ps1 -Local
```

Los secretos salen de `deploy/.env` (plantilla: `deploy/.env.example`). La primera vez tarda bastante: el clúster no comparte imágenes con Docker, y baja las de SQL Server y Keycloak por su cuenta.

Después, en un navegador con el aviso del certificado aceptado:

- Portal del cliente: https://portal.localhost:8443
- Panel del equipo: https://panel.localhost:8443
- Keycloak: https://keycloak.localhost:8443

## Mirarlo

```powershell
$env:KUBECONFIG = "$PWD\deploy\k8s\.kube\kubeconfig.yaml"   # kubectl apunta a este clúster, solo en esta terminal

kubectl -n helpdesk get pods,hpa,ingress
kubectl -n helpdesk logs deployment/api --tail=20           # una línea JSON por evento
kubectl -n helpdesk top pods
powershell -File deploy/k8s/helm.ps1 history helpdesk -n helpdesk
```

## Probarlo

```powershell
powershell -File tests/e2e/run.ps1 -Suite kubernetes -NoBuild
```

Mata pods, detiene SQL Server y Redis, y genera carga. Tarda unos 15 minutos, y deja el clúster como lo encontró.

## Detenerlo

```powershell
docker compose -f deploy/k8s/cluster.yaml stop      # conserva todo
docker compose -f deploy/k8s/cluster.yaml down -v   # borra el clúster, los datos y las imágenes que bajó
```

## Por qué está hecho así

**Las dos direcciones de Keycloak.** El navegador usa `https://keycloak.localhost:8443`, y los pods `http://keycloak:8080`, porque dentro del clúster `localhost` es el propio pod. `Keycloak__Authority` es la primera, y `Keycloak__MetadataAddress` la segunda.

**Lo que hace falta para tener varias réplicas:**

| Problema | Solución |
|---|---|
| Lo que cifra un pod no lo abre otro | Llaves de Data Protection compartidas en Redis |
| Un aviso nace en un pod de la API, y el cliente está en otro | Backplane de SignalR en Redis |
| `negotiate` cae en un pod y el WebSocket en otro | Los clientes se conectan directo por WebSocket (`skipNegotiation`) |
| El circuito de Blazor y sus tokens viven en un pod | Afinidad en el panel, con una cookie de Traefik |

**Probes.** `startup` da tiempo al primer arranque, `liveness` mira `/health/live` y no revisa dependencias, porque reiniciar no arregla una base caída, y `readiness` mira `/health/ready`.

**Apagado ordenado.** `preStop` espera 5 segundos mientras la noticia de que el pod se va llega a Traefik. Luego SIGTERM: ASP.NET Core deja de aceptar peticiones y termina las que tiene en curso.

**Migraciones.** Un Job en el hook `pre-upgrade` de Helm: si falla, las aplicaciones no se tocan. Por eso cada migración debe seguir sirviéndole al código anterior.

**SQL Server no llega a `restricted`.** Su binario lleva una capacidad de archivo, y Linux no lo deja ejecutar con `allowPrivilegeEscalation: false`. Las tres aplicaciones sí cumplen ese nivel.

## Límites

- **Con afinidad, un pod que se va da `502` unos instantes.** Traefik sigue mandando ahí a sus clientes hasta que el pod muere. El panel no se queda atascado: espera a que la página responda de forma estable, y recarga. Lo que el agente estaba escribiendo se pierde.
- **Las conexiones largas no se reparten.** El autoescalado agrega réplicas, pero un WebSocket o una conexión keep-alive sigue en el pod donde nació. Las réplicas nuevas reciben solo conexiones nuevas.
- **Las llaves de Data Protection quedan sin cifrar en Redis.** Un pod nuevo que arranque con Redis caído no puede leerlas. Los que ya corren las tienen en memoria.
- **Sin NetworkPolicy:** cualquier pod del clúster puede hablarle a SQL Server o a Redis.
- **Un Secret de Kubernetes solo está en Base64.** En producción, los secretos vienen de un almacén como Key Vault.
