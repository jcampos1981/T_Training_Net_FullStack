using System.Data.Common;
using System.Diagnostics.Metrics;
using Dapper;
using HelpDesk.Messaging;
using HelpDesk.ServiceDefaults;
using Microsoft.Data.SqlClient;

namespace HelpDesk.Worker;

public sealed record TicketEmail(string To, string Subject, string TicketCode);

public interface IEmailSender
{
    Task SendAsync(TicketEmail email, CancellationToken cancellationToken);
}

// Aquí iría SMTP o un servicio de correo. Por ahora queda en el log y en worker.SentEmails. Sin la dirección en el log: es un dato personal.
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(TicketEmail email, CancellationToken cancellationToken)
    {
        logger.LogInformation("Correo enviado sobre {TicketCode}: {Subject}", email.TicketCode, email.Subject);
        return Task.CompletedTask;
    }
}

// RabbitMQ entrega "al menos una vez": el mismo evento puede llegar dos veces, y el cliente no debe recibir dos correos. Registrar el mensaje y
// lo que produce va en una sola transacción: si algo falla no queda ninguno de los dos, y se puede reintentar.
//
// Límite: si el correo ya salió y se cae antes del commit, el reintento lo envía de nuevo. Un sistema externo no participa de la transacción, y
// lo único que evita eso del todo es que ese sistema reciba una llave de idempotencia. Para los datos propios, en cambio, el efecto es exacto.
public sealed class TicketEmailHandler(IConfiguration configuration, IEmailSender sender, TimeProvider clock)
{
    private const string RegisterMessage = "INSERT INTO worker.ProcessedMessages (MessageId, ProcessedAt) VALUES (@MessageId, @Now)";

    private const string SaveEmail = "INSERT INTO worker.SentEmails (MessageId, Recipient, Subject, TicketCode, SentAt) VALUES (@MessageId, @To, @Subject, @TicketCode, @Now)";

    private static readonly Counter<long> Sent = HelpDeskTelemetry.Meter.CreateCounter<long>("helpdesk.emails.sent", description: "Correos enviados a clientes");

    private static readonly Counter<long> Duplicates = HelpDeskTelemetry.Meter.CreateCounter<long>("helpdesk.events.duplicates", description: "Eventos que llegaron más de una vez");

    private readonly string connectionString = configuration.GetConnectionString("HelpDesk")
        ?? throw new InvalidOperationException("Falta ConnectionStrings:HelpDesk.");

    public async Task HandleAsync(Guid messageId, string eventType, TicketEventMessage message, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        // La llave primaria tiene IGNORE_DUP_KEY: un duplicado no es un error, simplemente inserta 0 filas. Dos entregas simultáneas se esperan
        // entre sí, y la segunda ve que ya está. Es el mismo recurso que usan las alertas de SLA.
        int inserted = await connection.ExecuteAsync(new CommandDefinition(RegisterMessage, new { MessageId = messageId, Now = now }, transaction, cancellationToken: cancellationToken));

        if (inserted == 0)
        {
            Duplicates.Add(1);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        // Un ticket creado por el equipo no tiene un cliente a quien avisarle.
        if (EmailFor(eventType, message) is { } email)
        {
            await sender.SendAsync(email, cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                SaveEmail,
                new { MessageId = messageId, email.To, email.Subject, email.TicketCode, Now = now },
                transaction,
                cancellationToken: cancellationToken));
            Sent.Add(1);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static TicketEmail? EmailFor(string eventType, TicketEventMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.CustomerEmail))
        {
            return null;
        }

        return eventType switch
        {
            TicketEventType.Created => new TicketEmail(message.CustomerEmail, $"Recibimos tu ticket {message.Code}: {message.Title}", message.Code),
            TicketEventType.Closed => new TicketEmail(message.CustomerEmail, $"Cerramos tu ticket {message.Code}: {message.Title}", message.Code),
            _ => null // un evento nuevo que este worker todavía no conoce: se ignora, no se reintenta para siempre
        };
    }
}
