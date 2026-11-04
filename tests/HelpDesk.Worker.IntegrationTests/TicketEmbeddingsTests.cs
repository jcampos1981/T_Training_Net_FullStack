using HelpDesk.Messaging;

namespace HelpDesk.Worker.IntegrationTests;

// Con SQL Server 2025 de verdad, y su tipo vector. Lo único falso es el modelo que calcula los números.
public sealed class TicketEmbeddingsTests(WorkerFactory worker)
{
    [Fact]
    public async Task ATicketClosed_IsIndexed_WithTheVectorOfItsProblem()
    {
        int ticketId = await worker.InsertTicketAsync("Impresora sin tóner", "No imprime nada desde ayer. Escríbeme a camila@acme.local");

        await worker.PublishAsync(Guid.NewGuid(), TicketEventType.Closed, EventFor(ticketId));

        Assert.True(await worker.WaitUntilIndexedAsync(ticketId));
        Assert.Equal(1, await worker.CountEmbeddingsAsync(ticketId));

        // Lo que se compara es el problema, y sin datos personales: el vector no debe poder relacionarse con el cliente.
        string text = Assert.Single(worker.Embeddings.TextsContaining("Impresora sin tóner"));
        Assert.Contains("No imprime nada desde ayer", text);
        Assert.DoesNotContain("camila@acme.local", text);
    }

    // RabbitMQ no deja cambiar los argumentos de una cola que ya existe: con una sola cola de reintentos, cambiar la espera dejaría al worker sin poder arrancar.
    [Fact]
    public async Task ChangingTheRetryDelay_DoesNotBreakTheQueues()
    {
        await worker.DeclareQueuesWithDelayAsync(TimeSpan.FromSeconds(7));

        int ticketId = await worker.InsertTicketAsync("Audífono sin sonido", "No se escucha nada.");
        await worker.PublishAsync(Guid.NewGuid(), TicketEventType.Closed, EventFor(ticketId));

        Assert.True(await worker.WaitUntilIndexedAsync(ticketId));
    }

    [Fact]
    public async Task TheSameEventTwice_LeavesOneRow()
    {
        int ticketId = await worker.InsertTicketAsync("Teclado sin respuesta", "No responde ninguna tecla.");
        var messageId = Guid.NewGuid();

        await worker.PublishAsync(messageId, TicketEventType.Closed, EventFor(ticketId));
        await worker.PublishAsync(messageId, TicketEventType.Closed, EventFor(ticketId));

        Assert.True(await worker.WaitUntilEmbeddedTimesAsync("Teclado sin respuesta", 2));
        Assert.Equal(1, await worker.CountEmbeddingsAsync(ticketId));
    }

    // Un ticket nuevo no es conocimiento todavía: su solución no existe.
    [Fact]
    public async Task ATicketJustCreated_IsNotIndexed()
    {
        int ticketId = await worker.InsertTicketAsync("Pantalla azul", "Se reinicia solo.");
        var marker = Guid.NewGuid();

        await worker.PublishAsync(Guid.NewGuid(), TicketEventType.Created, EventFor(ticketId));
        await worker.PublishAsync(marker, TicketEventType.Created, EventFor(ticketId));

        Assert.True(await worker.WaitUntilProcessedAsync(marker));
        Assert.Equal(0, await worker.CountEmbeddingsAsync(ticketId));
    }

    // Lo borraron después de cerrarlo: reintentar no lo traería de vuelta.
    [Fact]
    public async Task ATicketThatNoLongerExists_IsIgnored()
    {
        int missing = int.MaxValue - 1;
        var messageId = Guid.NewGuid();

        await worker.PublishAsync(messageId, TicketEventType.Closed, EventFor(missing));

        Assert.False(await worker.IsDeadAsync(messageId, TicketEventsTopology.EmbeddingsQueue, attempts: 20));
    }

    [Fact]
    public async Task WhenTheModelKeepsFailing_ItEndsInItsOwnDeadQueue_AndTheEmailStillGoesOut()
    {
        const string customer = "diego@acme.local";
        int ticketId = await worker.InsertTicketAsync("Modelo caído", "El modelo de embeddings no responde.");
        var messageId = Guid.NewGuid();
        worker.Embeddings.FailFor("Modelo caído");

        await worker.PublishAsync(messageId, TicketEventType.Closed, EventFor(ticketId, customer));

        Assert.True(await worker.IsDeadAsync(messageId, TicketEventsTopology.EmbeddingsQueue));
        Assert.Equal(0, await worker.CountEmbeddingsAsync(ticketId));

        // Cada consumidor tiene su cola: que falle uno no afecta al otro.
        Assert.True(await worker.WaitUntilProcessedAsync(messageId));
        Assert.Contains(worker.Emails.Sent, email => email.To == customer);
    }

    private static TicketEventMessage EventFor(int ticketId, string? customer = null) =>
        new(ticketId, $"T-{ticketId:D5}", "Ticket", customer, DateTimeOffset.UtcNow);
}
