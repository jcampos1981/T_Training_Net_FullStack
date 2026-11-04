# Evaluación de la respuesta sugerida

Mide si las sugerencias sirven, contra un modelo de verdad. No es una prueba: tarda minutos, no corre en CI, y que una respuesta cambie no significa que algo esté roto. Se corre al cambiar el modelo, el prompt o la distancia máxima, y se comparan los números.

```powershell
docker compose -f deploy/compose.yaml -f deploy/compose.app.yaml -f deploy/compose.ai.yaml run --rm ollama-models

dotnet run --project tests/HelpDesk.AiEval                             # búsqueda y respuestas
dotnet run --project tests/HelpDesk.AiEval -- --only-search            # solo la búsqueda: tarda segundos
dotnet run --project tests/HelpDesk.AiEval -- --chat-model otro:modelo # compara con otro modelo
dotnet run --project tests/HelpDesk.AiEval -- --tools                  # deja que el modelo pida otra búsqueda
dotnet run --project tests/HelpDesk.AiEval -- --claude claude-haiku-5-5  # con Claude: usa ANTHROPIC_API_KEY, y cobra
```

No usa la base de datos: compara los vectores en memoria, con la misma distancia del coseno que `VECTOR_DISTANCE`. Lo que corre contra SQL Server está en `tests/HelpDesk.Api.IntegrationTests/SuggestReplyTests.cs`, y de punta a punta en la suite `suggestions` de `tests/e2e`.

## Qué mide

`dataset.json` tiene 21 tickets resueltos y 26 preguntas: unas parecidas a un caso, otras en lenguaje coloquial, otras sin ninguna respuesta posible, y otras sobre un problema parecido pero con una solución que no sirve, que es donde más fácil se inventa:

| Mide | Cómo |
|---|---|
| **Búsqueda** | Si el caso correcto queda primero, o entre los 3 primeros, y el rango recíproco medio |
| **Distancia máxima** | Con cada umbral, cuántas preguntas con respuesta deja pasar, y cuántas sin respuesta. De aquí sale `Ai:MaxDistance` |
| **Usa la solución** | Si la respuesta contiene alguna palabra de la solución del caso |
| **Se rinde** | Si responde que no encontró nada, aunque había solución |
| **Reconoce que no sabe** | Si, sin un caso que sirva, responde que no encontró nada en vez de inventar |
| **Dice que ya se hizo** | Si le dice al cliente que algo ya se hizo en su ticket, copiando el tiempo pasado del caso |
| **Mezcla idiomas** | Si aparecen caracteres de otro idioma |
| **Inyección y datos ajenos** | Si obedece a un ticket que le da órdenes, o repite el correo y el teléfono de otro cliente |
| **Latencia y tokens** | Mediana y p95, hasta la primera palabra y hasta el final, y cuánto costaría lo mismo con Claude |

## Sus límites

- **Hay que leer las respuestas.** Buscar palabras clave solo dice si el modelo nombró lo que dice el caso, no si lo usó bien. "Ya no necesita desactivar los complementos" cuenta como un acierto, y una respuesta el doble de larga acierta más solo por tener más palabras. Las respuestas salen en la consola.
- 26 preguntas alcanzan para comparar y para notar un retroceso grande, no para dar un porcentaje preciso: una pregunta son 4 puntos.
- Las preguntas las escribió la misma persona que los casos. Tickets reales serán más difíciles.
- Se ajustó la distancia máxima con estas mismas preguntas. Con tickets reales, conviene volver a medirla.
- Los costos con Claude son una estimación con los tokens del modelo local, que los cuenta de otra forma.
