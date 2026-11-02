using Microsoft.Extensions.Options;

namespace HelpDesk.Admin.Authentication;

// Entrega un access token vigente para una sesión, renovándolo si hace falta. Es la pieza que hace posible que un
// circuito abierto durante horas siga llamando a la API sin recargar la página.
public sealed class SessionAccessTokens(
    SessionTokenStore store,
    AccessTokenRefresher refresher,
    IOptions<KeycloakClientOptions> options,
    TimeProvider clock)
{
    // null = la sesión terminó: no está en el almacén, o Keycloak ya no la reconoce.
    public async Task<string?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (store.Find(sessionId) is not { } session)
        {
            return null;
        }

        if (!IsAboutToExpire(session.Tokens))
        {
            return session.Tokens.AccessToken;
        }

        // Una pantalla hace varias llamadas en paralelo (el ticket y su conversación). Sin el candado, cada una
        // renovaría por su cuenta; y si Keycloak rota los refresh tokens, la segunda cerraría la sesión.
        await session.RefreshLock.WaitAsync(cancellationToken);
        try
        {
            // Mientras esperaba el candado, otra llamada pudo haberlo renovado.
            if (!IsAboutToExpire(session.Tokens))
            {
                return session.Tokens.AccessToken;
            }

            if (await refresher.RefreshAsync(session.Tokens, cancellationToken) is not { } refreshed)
            {
                store.Remove(sessionId);
                return null;
            }

            session.Tokens = refreshed;
            return refreshed.AccessToken;
        }
        finally
        {
            session.RefreshLock.Release();
        }
    }

    private bool IsAboutToExpire(SessionTokens tokens) =>
        tokens.ExpiresAt - clock.GetUtcNow() <= options.Value.RefreshBeforeExpiry;
}
