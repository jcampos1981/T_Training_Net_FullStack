using HelpDesk.Messaging;
using RabbitMQ.Client;

namespace HelpDesk.Api.Outbox;

// Una sola conexión para toda la aplicación: abrirla es caro, y RabbitMQ espera que duren. Un canal no se puede compartir entre hilos, pero
// aquí solo lo usa el publicador, y sus vueltas nunca se solapan.
public sealed class RabbitMqPublisherChannel(string connectionString) : IAsyncDisposable
{
    private IConnection? connection;
    private IChannel? channel;

    public async Task<IChannel> GetAsync(CancellationToken cancellationToken)
    {
        if (channel is { IsOpen: true })
        {
            return channel;
        }

        await CloseAsync();

        var factory = new ConnectionFactory
        {
            Uri = new Uri(connectionString),
            ClientProvidedName = "helpdesk-api",
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5)
        };

        connection = await factory.CreateConnectionAsync(cancellationToken);
        channel = await connection.CreateChannelAsync(
            // Confirmaciones: BasicPublishAsync termina cuando RabbitMQ aceptó el mensaje, y lanza una excepción si lo rechazó.
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);

        await TicketEventsTopology.DeclareEventsExchangeAsync(channel, cancellationToken);

        return channel;
    }

    public async ValueTask DisposeAsync() => await CloseAsync();

    private async Task CloseAsync()
    {
        if (channel is not null)
        {
            await channel.DisposeAsync();
            channel = null;
        }

        if (connection is not null)
        {
            await connection.DisposeAsync();
            connection = null;
        }
    }
}
