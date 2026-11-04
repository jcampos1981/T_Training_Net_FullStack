using System.Diagnostics;
using System.Net;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.IntegrationTests.Support;
using Microsoft.Data.SqlClient;

namespace HelpDesk.Api.IntegrationTests;

public sealed class RequestTimeoutTests(HelpDeskApiFactory api)
{
    // Una fila bloqueada de verdad: la base no responde, y sin el límite la petición esperaría los 30 segundos del comando, más los reintentos de EF.
    [Fact]
    public async Task ARequestThatTakesTooLong_IsCut_AndTheApiKeepsWorking()
    {
        ApiUser laura = await api.AsAsync("laura");
        TicketResponse ticket = await laura.CreateTicketAsync(Test.UniqueTitle("Bloqueado"));

        await using (var blocker = new SqlConnection(api.ConnectionString))
        {
            await blocker.OpenAsync(Test.Cancel);
            await using SqlTransaction transaction = blocker.BeginTransaction();

            await using (SqlCommand update = blocker.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE Tickets SET Title = Title WHERE Id = @id";
                update.Parameters.AddWithValue("@id", ticket.Id);
                await update.ExecuteNonQueryAsync(Test.Cancel);
            }

            var elapsed = Stopwatch.StartNew();
            HttpResponseMessage response = await laura.UpdateTicketAsync(ticket.Id, title: Test.UniqueTitle("Otro"), version: ticket.Version);
            elapsed.Stop();

            response.ShouldBe(HttpStatusCode.ServiceUnavailable);
            Assert.InRange(elapsed.Elapsed.TotalSeconds, 14, 25);

            await transaction.RollbackAsync(Test.Cancel);
        }

        // La cancelación llegó hasta la consulta: no quedó nada a medias.
        (await laura.UpdateTicketAsync(ticket.Id, title: Test.UniqueTitle("Ahora sí"), version: ticket.Version)).ShouldBe(HttpStatusCode.OK);
    }
}
