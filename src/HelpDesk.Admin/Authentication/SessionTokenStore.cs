using Microsoft.Extensions.Caching.Memory;

namespace HelpDesk.Admin.Authentication;

// Los tokens de una sesión. Inmutable: renovar es reemplazar el registro completo.
public sealed record SessionTokens(string AccessToken, string RefreshToken, string IdToken, DateTimeOffset ExpiresAt);

// Una sesión en el almacén: sus tokens actuales y el candado para que solo una llamada a la vez los renueve.
public sealed class TokenSession(SessionTokens tokens)
{
    private volatile SessionTokens _tokens = tokens;

    public SessionTokens Tokens
    {
        get => _tokens;
        set => _tokens = value;
    }

    public SemaphoreSlim RefreshLock { get; } = new(1, 1);
}

// Por qué los tokens viven aquí y no en la cookie, como en el BFF:
// el BFF recibe una petición HTTP por cada llamada, así que puede leer la cookie y reescribirla con tokens nuevos.
// Blazor Server trabaja sobre un WebSocket que dura horas: dentro del circuito no hay petición ni respuesta, por lo
// tanto no hay cookie que leer ni forma de actualizarla. El circuito solo conoce los claims con que se conectó.
// La cookie guarda entonces la identidad (con su "sid"), y los tokens quedan en el servidor, bajo ese "sid".
//
// En memoria: si el panel se reinicia, las sesiones se pierden y Keycloak vuelve a entregar tokens al siguiente
// request (ver AdminCookieEvents). Con varias instancias iría a Redis, cifrado con Data Protection (Fase 7).
public sealed class SessionTokenStore(IMemoryCache cache)
{
    // Igual que la cookie: una jornada, y se extiende si hay actividad.
    private static readonly MemoryCacheEntryOptions Lifetime = new() { SlidingExpiration = AdminAuthentication.SessionLifetime };

    public void Save(string sessionId, SessionTokens tokens) =>
        cache.Set(Key(sessionId), new TokenSession(tokens), Lifetime);

    public TokenSession? Find(string sessionId) =>
        cache.Get<TokenSession>(Key(sessionId));

    public void Remove(string sessionId) =>
        cache.Remove(Key(sessionId));

    private static string Key(string sessionId) => $"session-tokens:{sessionId}";
}
