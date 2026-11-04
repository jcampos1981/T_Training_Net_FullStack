# ADR 0002: Eventos con outbox y un worker idempotente

**Estado:** aceptada.

## Contexto

Al crear o cerrar un ticket hay que avisarle al cliente por correo. Enviar el correo dentro de la petición la haría esperar a un servidor externo, y un fallo de ese servidor impediría crear el ticket. Por eso va a un proceso aparte, y se comunican por RabbitMQ.

Eso crea un problema nuevo: guardar en SQL Server y publicar en RabbitMQ son dos sistemas, y no hay una transacción que abarque ambos.

| Orden | Si se cae entre medio |
|---|---|
| Guardar y después publicar | El ticket queda sin evento, y el cliente nunca se entera |
| Publicar y después guardar | Hay un evento de un ticket que no existe |

## Decisión

**1. Patrón outbox.** El evento se escribe en la tabla `OutboxMessages`, en la misma transacción que el ticket. Un trabajo en segundo plano publica lo pendiente, y lo marca cuando RabbitMQ confirma.

Lo escribe un interceptor de EF Core (`OutboxInterceptor`), y no cada caso de uso. Así no hay forma de crear un ticket y olvidarse del evento.

**2. Entrega "al menos una vez".** Si el publicador se cae entre publicar y marcar, publica de nuevo. "Exactamente una vez" no existe entre dos sistemas, así que el duplicado se resuelve del otro lado.

**3. Consumidor idempotente.** El worker registra cada `MessageId` en la misma transacción que su efecto, con una llave primaria `IGNORE_DUP_KEY`. Un mensaje repetido inserta 0 filas, y se sabe que ya se procesó.

**4. Reintentos con espera, y cola de muertos.** Un mensaje que falla espera en una cola sin consumidores, y vuelve solo al vencer su TTL. Reintentar enseguida seguiría fallando y bloquearía a los demás. Después de 4 intentos queda en `helpdesk.ticket-emails.dead`, para revisarlo a mano.

**5. `mandatory: true` al publicar.** Sin eso, si todavía no hay ninguna cola enlazada, RabbitMQ descarta el mensaje y responde que todo salió bien. Con eso falla, y el evento sigue pendiente.

## Consecuencias

**A favor:**

- Crear un ticket no depende de RabbitMQ ni del servidor de correo. Con RabbitMQ caído no se pierde nada: los eventos esperan en la tabla. La suite `containers` lo prueba.
- La traza continúa a través de la cola, porque el outbox guarda el `traceparent` de la petición.

**En contra:**

- **El aviso no es inmediato:** el publicador pasa cada 5 segundos.
- **Más piezas:** una tabla, un trabajo en segundo plano, un worker y un broker.
- **Un `ExecuteUpdate` no pasa por el interceptor.** Hoy ninguno cambia el estado de un ticket. Uno que lo haga tendría que escribir su evento a mano.
- **El efecto exacto vale para los datos propios.** Si el correo ya salió y el worker se cae antes del commit, el reintento lo envía de nuevo. Evitarlo del todo exige que el servicio de correo reciba una llave de idempotencia.
- **El orden no está garantizado.** Con reintentos o varias instancias, un `ticket.closed` puede procesarse antes que su `ticket.created`. Aquí no importa, porque cada correo es independiente. Un consumidor al que le importe tendría que comparar versiones.

## Alternativas descartadas

- **Enviar el correo en la petición.** Es lo más simple, pero ata la creación del ticket a un sistema externo.
- **Una cola en memoria dentro de la API.** Sin broker ni worker, pero se pierde todo al reiniciar y no se reparte entre instancias.
- **Change Data Capture con Debezium.** Lee el log de transacciones y no hace falta tocar el código, pero es una infraestructura grande para dos eventos.
