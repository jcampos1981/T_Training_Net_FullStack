# HelpDesk con Aspire

Levanta toda la aplicación en desarrollo con un solo comando, y abre un dashboard con sus logs, trazas y métricas. Es para desarrollo: para desplegar están `deploy/helm/helpdesk` y `deploy/azure`.

```powershell
# Comparte puertos y el volumen de SQL Server con Compose: no pueden correr a la vez.
docker compose -f deploy/compose.yaml stop

dotnet run --project src/HelpDesk.AppHost --launch-profile http
```

Solo hace falta Docker, Node y `deploy/.env` (plantilla: `deploy/.env.example`). La primera vez tarda varios minutos, porque construye la imagen del migrador.

| | |
|---|---|
| Dashboard | http://localhost:15888 |
| Portal del cliente | https://localhost:7200 |
| Panel del equipo | https://localhost:7300 |
| Keycloak | http://localhost:8080 |
| RabbitMQ | http://localhost:15672 (usuario `guest`, contraseña `RABBITMQ_PASSWORD`) |

Para terminar, Ctrl+C en esa terminal: Aspire borra sus contenedores. Los datos de SQL Server quedan en el volumen.

## Qué levanta

SQL Server, Redis, RabbitMQ y Keycloak, en contenedores. Después el migrador, que corre y termina; la API y el worker, que lo esperan; el portal, que se compila una vez y lo sirve el BFF; y el BFF y el panel. Cada aplicación recibe sus direcciones como variables de entorno.

## Para ver una traza de punta a punta

1. Inicia sesión en el portal, como `camila`, y abre un ticket.
2. En el dashboard, abre **Seguimientos** y busca `helpdesk-bff: POST /api/{**catch-all}`.

Ves una sola traza que pasa por el BFF, la API, el caso de uso `OpenTicket`, su SQL, RabbitMQ y el worker. Los segundos que faltan entre `INSERT` y `MSG rabbitmq` son los que el evento espera en el outbox.

## Ojo

- **El dashboard no pide contraseña** con este perfil, porque solo escucha en esta máquina. Muestra logs que pueden traer datos personales: en `Development`, EF Core registra los valores de los parámetros SQL.
- **Solo las aplicaciones de .NET envían trazas.** Lo que pasa en el navegador no aparece: la traza empieza en el BFF.
- **Sin el servidor de Vite:** Keycloak solo acepta su puerto 5173, que puede estar ocupado. Para trabajar en el portal con recarga en caliente, usa `npm run dev` con Compose.
