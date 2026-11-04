# Servidor MCP de HelpDesk

Le da a un asistente, como Claude Code, acceso de solo lectura a los tickets, con [Model Context Protocol](https://modelcontextprotocol.io). El asistente arranca este programa como un proceso, y conversan por stdin y stdout.

| Herramienta | Qué hace |
|---|---|
| `listar_tickets` | Los tickets que la cuenta puede ver, con filtros por estado y prioridad. Van del más antiguo al más nuevo, como en la API, de a 50 como máximo |
| `ver_ticket` | Un ticket y sus comentarios |

## Cómo usarlo

La API tiene que estar corriendo. La cuenta se guarda en User Secrets, fuera del repositorio y de la configuración del asistente:

```powershell
dotnet user-secrets set "HelpDeskApi:Email" "laura@helpdesk.local" --project src/HelpDesk.Mcp
dotnet user-secrets set "HelpDeskApi:Password" "..." --project src/HelpDesk.Mcp

claude mcp add helpdesk -- dotnet run --no-build --project src/HelpDesk.Mcp
```

`HelpDeskApi:BaseUrl` cambia la dirección de la API, que por defecto es `http://localhost:5159`.

## Decisiones

- **Pasa por la API, con la cuenta de una persona.** El asistente ve lo mismo que esa persona: un agente, sus tickets; un supervisor, todos.
- **Solo lee.** Cualquier cliente escribe en un ticket, y un texto malicioso podría convencer al asistente de hacer algo. Sin herramientas que escriban, lo peor que consigue es una mala respuesta.
- **Oculta datos personales.** No devuelve quién pidió el ticket, y oculta correos y teléfonos en los textos. Si el asistente corre en la nube, lo que devuelve este servidor sale de la empresa.
