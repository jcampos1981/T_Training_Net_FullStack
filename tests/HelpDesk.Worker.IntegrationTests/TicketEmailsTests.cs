using System.Text;
using HelpDesk.Messaging;
using HelpDesk.Worker;

namespace HelpDesk.Worker.IntegrationTests;

public sealed class TicketEmailsTests(WorkerFactory worker)
{
    private const string Customer = "camila@acme.local";

    private static int nextTicketId;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ATicketCreated_SendsAnEmailToTheCustomer()
    {
        var messageId = Guid.NewGuid();
        TicketEventMessage message = NewMessage(Customer);

        await worker.PublishAsync(messageId, TicketEventType.Created, message);

        Assert.True(await worker.WaitUntilProcessedAsync(messageId));
        Assert.Contains($"Recibimos tu ticket {message.Code}", await worker.SubjectOfAsync(messageId));
        Assert.Single(worker.Emails.Sent, email => email.TicketCode == message.Code && email.To == Customer);
    }

    [Fact]
    public async Task ATicketClosed_SendsAnEmailToTheCustomer()
    {
        var messageId = Guid.NewGuid();

        await worker.PublishAsync(messageId, TicketEventType.Closed, NewMessage(Customer));

        Assert.True(await worker.WaitUntilProcessedAsync(messageId));
        Assert.StartsWith("Cerramos tu ticket", await worker.SubjectOfAsync(messageId));
    }

    // Lo normal con "al menos una vez": se publicó y se cayó antes de marcarlo, o el worker se cayó antes de confirmar.
    [Fact]
    public async Task TheSameMessageTwice_SendsOnlyOneEmail()
    {
        var messageId = Guid.NewGuid();
        TicketEventMessage message = NewMessage(Customer);
        var marker = Guid.NewGuid();

        await worker.PublishAsync(messageId, TicketEventType.Created, message);
        await worker.PublishAsync(messageId, TicketEventType.Created, message);

        // La cola es FIFO: cuando se procesa este, los dos anteriores ya pasaron.
        await worker.PublishAsync(marker, TicketEventType.Created, NewMessage(customer: null));
        Assert.True(await worker.WaitUntilProcessedAsync(marker));

        Assert.Single(worker.Emails.Sent, email => email.TicketCode == message.Code);
        Assert.Equal(1, await worker.CountAsync("worker.SentEmails", messageId));
        Assert.Equal(1, await worker.CountAsync("worker.ProcessedMessages", messageId));
    }

    [Fact]
    public async Task ATicketWithoutACustomer_SendsNothing()
    {
        var messageId = Guid.NewGuid();
        TicketEventMessage message = NewMessage(customer: null);

        await worker.PublishAsync(messageId, TicketEventType.Created, message);

        Assert.True(await worker.WaitUntilProcessedAsync(messageId));
        Assert.Equal(0, await worker.CountAsync("worker.SentEmails", messageId));
        Assert.DoesNotContain(worker.Emails.Sent, email => email.TicketCode == message.Code);
    }

    // Un evento más nuevo que este worker: se ignora. Si fallara, volvería una y otra vez, y terminaría en la cola de muertos sin razón.
    [Fact]
    public async Task AnEventThisWorkerDoesNotKnow_IsIgnored()
    {
        var messageId = Guid.NewGuid();

        await worker.PublishAsync(messageId, "ticket.reopened", NewMessage(Customer));

        Assert.True(await worker.WaitUntilProcessedAsync(messageId));
        Assert.Equal(0, await worker.CountAsync("worker.SentEmails", messageId));
    }

    [Fact]
    public async Task AFailure_IsRetried_AndTheEmailGoesOutOnce()
    {
        var messageId = Guid.NewGuid();
        TicketEventMessage message = NewMessage(Customer);
        worker.Emails.FailFor(message.Code, times: 1);

        await worker.PublishAsync(messageId, TicketEventType.Created, message);

        Assert.True(await worker.WaitUntilProcessedAsync(messageId));
        Assert.Single(worker.Emails.Sent, email => email.TicketCode == message.Code);
        Assert.Equal(1, await worker.CountAsync("worker.SentEmails", messageId));
    }

    // Lo que falla el registro también se revierte: si no, el reintento creería que ya está hecho, y el correo nunca saldría.
    [Fact]
    public async Task WhenItKeepsFailing_ItEndsInTheDeadQueue_WithoutMarkingItAsDone()
    {
        var messageId = Guid.NewGuid();
        TicketEventMessage message = NewMessage(Customer);
        worker.Emails.FailFor(message.Code, times: 100);

        await worker.PublishAsync(messageId, TicketEventType.Created, message);

        Assert.True(await worker.IsDeadAsync(messageId));
        Assert.Equal(0, await worker.CountAsync("worker.ProcessedMessages", messageId));
        Assert.Equal(0, await worker.CountAsync("worker.SentEmails", messageId));
    }

    [Fact]
    public async Task AMessageThatCannotBeRead_EndsInTheDeadQueue()
    {
        var messageId = Guid.NewGuid();

        await worker.PublishRawAsync(messageId, TicketEventType.Created, Encoding.UTF8.GetBytes("esto no es JSON"));

        Assert.True(await worker.IsDeadAsync(messageId));
    }

    private static TicketEventMessage NewMessage(string? customer)
    {
        int id = Interlocked.Increment(ref nextTicketId);

        return new TicketEventMessage(id, $"HD-{id:D5}", "Impresora sin tóner", customer, DateTimeOffset.UtcNow);
    }
}
