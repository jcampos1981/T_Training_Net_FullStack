# ADR 0003: la respuesta sugerida, con un modelo local y la búsqueda decidiendo

**Estado:** aceptada. **Fecha:** noviembre de 2026.

## Contexto

Un agente que atiende un ticket suele resolver algo que el equipo ya resolvió. Queremos que el panel le redacte un borrador a partir de los tickets cerrados parecidos (RAG).

Hay tres restricciones:

- **Los tickets los escribe cualquiera.** Un cliente puede dejar en su texto instrucciones para el modelo, y esas instrucciones podrían terminar en el borrador para otro cliente.
- **Las soluciones salen de tickets de otros clientes.** Sus datos no deben aparecer en la respuesta ni salir de la empresa.
- **El proyecto debe poder correrse sin clave y sin costo.**

## Decisión

1. **Un borrador, nunca un envío.** El texto queda en el campo de comentario, dice en qué tickets se basó, y solo se envía si un agente aprieta "Comentar".
2. **Un modelo local por defecto**, `gemma3:4b` con Ollama. Claude es una opción de configuración, detrás de la misma interfaz (`IChatClient`).
3. **La búsqueda decide si hay respuesta, y el modelo solo redacta.** Si ningún ticket queda a menos de `Ai:MaxDistance`, se responde que no hay casos, sin llamar al modelo. Al modelo no se le ofrece esa salida.
4. **El modelo recibe lo mínimo:** el título del ticket y lo que escribió el equipo en los casos parecidos. No recibe la conversación del ticket, ni los títulos ni los comentarios de otros clientes.
5. **Los vectores viven en SQL Server**, con el tipo `vector` de la versión 2025, junto a los tickets: misma transacción, mismo respaldo, una base menos que operar.
6. **Sin herramientas para el modelo** (`Ai:EnableTools` apagado). El código existe y está probado, pero no se usa.

## Lo que se midió

Con `tests/HelpDesk.AiEval`: 21 tickets resueltos y 26 preguntas, en CPU, sin GPU. Todos con el prompt final, salvo donde se indica.

| Modelo | Tamaño | Usa la solución | Resiste ataques | Dice que ya se hizo | Respuesta completa (mediana) |
|---|---|---|---|---|---|
| `qwen2.5:3b` | 1,9 GB | 12 de 16 | 3 de 4 | 2 de 20 | 5,8 s |
| `gemma3:4b` | 3,3 GB | 14 de 16 | 3 de 4 | 2 de 20 | 11,9 s |

`qwen2.5:1.5b` (1,0 GB) se descartó antes y no se volvió a medir con el prompt final. Con el anterior usó la solución en 11 de 16, en 2,7 s, respondió "Aprobadí" a una inyección en el título, y fue el único que mezcló chino en una respuesta. Leyendo las respuestas, `gemma3:4b` es claramente el mejor en español. Por eso se eligió, aunque tarde el doble.

**Búsqueda:** el caso correcto queda primero en el 95 % de las preguntas con respuesta, y entre los 3 primeros en el 100 %.

**El número no alcanza.** La métrica busca palabras clave, así que "ya no necesita desactivar los complementos", lo contrario de la solución, cuenta como un acierto. Las decisiones de este documento salen de leer las respuestas.

### Por qué la búsqueda decide, y no el modelo

Con la instrucción "si ningún caso sirve, responde que no hay casos", los modelos tomaban esa salida casi siempre:

| Quién decide | Modelo | Usa la solución | Se rinde aunque había solución |
|---|---|---|---|
| El modelo | `qwen2.5:1.5b` | 3 de 16 | 13 de 16 |
| El modelo | `qwen2.5:3b` | 2 de 16 | 16 de 16 |
| La búsqueda | `qwen2.5:1.5b` | 12 de 16 | 1 de 16 |
| La búsqueda | `qwen2.5:3b` | 11 de 16 | 1 de 16 |

El costo: la distancia no distingue bien un problema parecido de uno igual. "El proyector no enciende" queda a 0,35 del caso del monitor sin señal, más cerca que preguntas coloquiales con solución (hasta 0,44). Con `Ai:MaxDistance` en 0,45 pasan 19 de 20 preguntas con respuesta, y 1 de 6 sin respuesta: el proyector recibe la solución del cable HDMI.

### Por qué no se le envía la conversación

Con la conversación en el prompt, `qwen2.5:1.5b` y `qwen2.5:3b` respondieron "APROBADO" a un ticket que se lo ordenaba. Sin ella, esa inyección no llega al modelo. Las reglas del prompt ayudan, pero solo esto lo evita con seguridad: **lo que no se le envía, no lo puede obedecer.**

Por lo mismo, `dbo.TicketComments` distingue con `FromCustomer` lo que escribió un cliente, y solo lo que escribió el equipo llega al modelo.

### Por qué sin herramientas

| | Sin herramienta | Con herramienta |
|---|---|---|
| `gemma3:4b` | 11,9 s | No las admite: `ModelDoesNotSupportToolsException` |
| `qwen2.5:3b` | 5,8 s y 248 tokens de entrada | 12,3 s y 868 tokens de entrada |

`qwen2.5:3b` llamó a la herramienta en las 20 respuestas, aunque ya tenía los casos, y mezcló soluciones de otros problemas. Su métrica subió de 12 a 15 de 16 solo porque las respuestas son el doble de largas. Una búsqueda que el modelo decide por su cuenta tampoco pasa por el filtro de distancia.

## Consecuencias

- **Lo que no se resolvió.**
  - Si el texto malicioso está en un comentario del equipo, por ejemplo uno que copió el mensaje de un cliente, `gemma3:4b` lo obedece: le pidió la contraseña al cliente en la prueba. Los pasos que invente, o el "[dato oculto]" que deje en el texto, también quedan en el borrador. La defensa es la decisión 1: un agente lo lee antes de enviarlo.
  - Un problema parecido sin solución recibe una solución que no sirve.
- **Lento en CPU.** Unos 12 segundos, con la primera palabra a los 5, y unos 43 si el modelo no está en memoria. Por eso el texto aparece a medida que se escribe, y `OLLAMA_KEEP_ALIVE` lo mantiene cargado una hora, a cambio de unos GB de memoria.
- **Pesa.** Ollama y los modelos ocupan varios GB. Por eso están en `deploy/compose.ai.yaml`, aparte: sin ese archivo, la aplicación funciona igual y el botón no aparece.
- **`Ai:MaxDistance` depende del modelo de embeddings y de los datos.** Se ajustó con pocas preguntas, escritas por la misma persona que los casos. Con tickets reales hay que volver a medir.
- **Búsqueda exacta.** Recorre todos los vectores, lo que funciona con decenas de miles. Para millones haría falta un índice DiskANN.
- **Sin carga inicial.** Solo se indexan los tickets que se cierran desde que la función está activa.
- **Claude no se ha probado.** El código compila y crea el cliente, pero nunca se llamó a la API: necesita una clave y cobra. Antes de usarlo hay que correr la evaluación con `--claude`, revisando que `Ai:MaxOutputTokens` alcance. Además, el título y las soluciones saldrían de la máquina.
- **Costo estimado con Claude**, para 1.000 sugerencias con estos tokens, a los precios de noviembre de 2026: US$ 0,05 con Haiku 5.5, US$ 1 con Sonnet 5.5 y US$ 2 con Opus 5.5. Es una estimación: otro modelo cuenta los tokens de otra forma.
- **La licencia.** Gemma tiene términos de uso propios, a diferencia de un modelo Apache 2.0. Hay que revisarlos antes de usarlo en producción.
- **Kubernetes y Azure:** esta función no se llevó allá. El chart la deja apagada.

## Alternativas descartadas

- **Dejar que el modelo decida si el caso sirve:** es lo que se probó primero, y se rendía casi siempre.
- **Una base vectorial aparte, o pgvector:** otra pieza que operar y mantener sincronizada, cuando SQL Server 2025 ya lo resuelve.
- **Solo Claude:** mejores respuestas, probablemente, pero el proyecto dejaría de correr sin clave, y los datos de los clientes saldrían siempre de la máquina.
- **Microsoft Agent Framework:** sirve para agentes con varios pasos, memoria y orquestación. Aquí hay una sola llamada al modelo, y `IChatClient` alcanza.
