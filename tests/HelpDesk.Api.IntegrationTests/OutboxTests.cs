using System.Net;
using HelpDesk.Api.Data;
using HelpDesk.Api.Features.Portal;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.IntegrationTests.Support;
using HelpDesk.Api.Outbox;
using HelpDesk.Domain;
using HelpDesk.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HelpDesk.Api.IntegrationTests;

// Con RabbitMQ de verdad: lo que se prueba es lo que llega a la cola, no que alguien llamó a un método.
public sealed class OutboxTests(HelpDeskApiFactory api)
{
    [Fact]
    public async Task CreatingATicket_PublishesTheEvent()
    {
        ApiUser laura = await api.AsAsync("laura");
        string title = Test.UniqueTitle("Se crea");

        TicketResponse ticket = await laura.CreateTicketAsync(title);

        ReceivedTicketEvent created = await api.Events.WaitForAsync(ticket.Id, TicketEventType.Created);
        Assert.Equal(title, created.Message.Title);
        Assert.Null(created.Message.CustomerEmail);
        Assert.True(Guid.TryParse(created.MessageId, out _));
    }

    [Fact]
    public async Task OpeningATicketFromThePortal_CarriesTheEmailOfTheCustomer()
    {
        ApiUser camila = await api.AsAsync("camila");

        PortalTicketResponse ticket = await camila.OpenTicketAsync(Test.UniqueTitle("Desde el portal"));

        ReceivedTicketEvent created = await api.Events.WaitForAsync(ticket.Id, TicketEventType.Created);
        Assert.Equal("camila@acme.local", created.Message.CustomerEmail);
    }

    [Fact]
    public async Task ClosingATicket_PublishesOneEvent_AndOtherChangesPublishNone()
    {
        ApiUser laura = await api.AsAsync("laura");
        TicketResponse ticket = await laura.CreateTicketAsync(Test.UniqueTitle("Se cierra"));

        TicketResponse edited = await (await laura.UpdateTicketAsync(ticket.Id, title: Test.UniqueTitle("Otro título"))).ReadAsync<TicketResponse>();
        await (await laura.UpdateTicketAsync(ticket.Id, status: TicketStatus.InProgress, version: edited.Version)).ReadAsync<TicketResponse>();
        Assert.Empty(await api.OutboxRowsAsync(ticket.Id, TicketEventType.Closed));

        await (await laura.UpdateTicketAsync(ticket.Id, status: TicketStatus.Closed)).ReadAsync<TicketResponse>();

        await api.Events.WaitForAsync(ticket.Id, TicketEventType.Closed);
        Assert.Single(await api.OutboxRowsAsync(ticket.Id, TicketEventType.Closed));
    }

    // La razón de ser del outbox: el evento es parte de la transacción. Si el cambio no se guarda, no hay evento.
    [Fact]
    public async Task AChangeThatIsNotSaved_LeavesNoEvent()
    {
        ApiUser laura = await api.AsAsync("laura");
        TicketResponse ticket = await laura.CreateTicketAsync(Test.UniqueTitle("Conflicto"));
        await (await laura.UpdateTicketAsync(ticket.Id, title: Test.UniqueTitle("Guardó primero"))).ReadAsync<TicketResponse>();

        HttpResponseMessage late = await laura.UpdateTicketAsync(ticket.Id, status: TicketStatus.Closed, version: ticket.Version);

        late.ShouldBe(HttpStatusCode.Conflict);
        Assert.Empty(await api.OutboxRowsAsync(ticket.Id, TicketEventType.Closed));
    }

    [Fact]
    public async Task ThePublishedEvent_IsMarked_AndContinuesTheTrace()
    {
        ApiUser laura = await api.AsAsync("laura");
        TicketResponse ticket = await laura.CreateTicketAsync(Test.UniqueTitle("Con traza"));

        ReceivedTicketEvent created = await api.Events.WaitForAsync(ticket.Id, TicketEventType.Created);

        Assert.StartsWith("00-", created.TraceParent); // formato W3C: 00-<trace>-<span>-<flags>

        OutboxMessage row = Assert.Single(await api.OutboxRowsAsync(ticket.Id, TicketEventType.Created));
        Assert.Equal(created.TraceParent?.Split('-')[1], row.TraceParent?.Split('-')[1]); // la misma traza que la petición que lo creó
        await WaitUntilMarkedAsync(row.MessageId);
    }

    private async Task WaitUntilMarkedAsync(Guid messageId)
    {
        await using AsyncServiceScope scope = api.Services.CreateAsyncScope();
        HelpDeskDbContext db = scope.ServiceProvider.GetRequiredService<HelpDeskDbContext>();

        for (int attempt = 0; attempt < 50; attempt++)
        {
            if (await db.OutboxMessages.AnyAsync(message => message.MessageId == messageId && message.PublishedAt != null, Test.Cancel))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), Test.Cancel);
        }

        Assert.Fail("El evento se publicó, pero quedó sin marcar: se volvería a publicar.");
    }
}

// Sola: mientras dura, ninguna cola recibe los eventos, y la API está Degraded. Otra prueba en paralelo lo notaría.
[Collection(SequentialCollection.Name)]
public sealed class OutboxWithoutQueueTests(HelpDeskApiFactory api)
{
    // Sin una cola enlazada, RabbitMQ descarta el mensaje y responde que todo salió bien. El evento tiene que seguir pendiente, no perderse.
    [Fact]
    public async Task WithNoQueueToReceiveIt_TheEventStaysPending_UntilThereIsOne()
    {
        ApiUser laura = await api.AsAsync("laura");
        await api.Events.UnbindAsync();
        TicketResponse ticket;

        try
        {
            ticket = await laura.CreateTicketAsync(Test.UniqueTitle("Sin cola"));

            await Task.Delay(TimeSpan.FromSeconds(2), Test.Cancel); // varias vueltas del publicador

            OutboxMessage pending = Assert.Single(await api.OutboxRowsAsync(ticket.Id, TicketEventType.Created));
            Assert.False(await api.IsPublishedAsync(pending.MessageId));
        }
        finally
        {
            await api.Events.BindAsync();
        }

        await api.Events.WaitForAsync(ticket.Id, TicketEventType.Created);
    }
}
