using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace HelpDesk.Api.Security;

// Rate limiting integrado de ASP.NET Core: cuántas peticiones acepta la API por usuario y por tiempo.
// Protege la API (y la base) de un cliente con un bucle descontrolado, de scrapers y de ataques de fuerza bruta.
public static class RateLimitingSetup
{
    // Política para login y emisión de tokens: se aplica con .RequireRateLimiting(Login) en esos endpoints.
    public const string Login = "login";

    private static readonly TimeSpan LoginWindow = TimeSpan.FromMinutes(1);
    private const int LoginWindowSegments = 6; // tramos de 10 s

    public static IServiceCollection AddHelpDeskRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<RateLimitingOptions>()
            .BindConfiguration(RateLimitingOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            // Por defecto responde 503 ("servicio no disponible"), que sugiere una caída. 429 dice "vas muy rápido".
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = WriteTooManyRequestsAsync;
        });

        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<RateLimitingOptions>>((limiter, limits) =>
            {
                RateLimitingOptions value = limits.Value;

                // Global: corre en TODAS las peticiones, antes de cualquier política de endpoint (se suman las dos).
                limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    RateLimitPartition.GetTokenBucketLimiter(ClientKey(context), _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = value.UserBurst,
                        // Repone de a poco (cada 10 s) en vez de todo junto al minuto: el ritmo queda parejo.
                        TokensPerPeriod = value.UserRequestsPerMinute / 6,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                        QueueLimit = 0 // sin fila de espera: rechaza al instante y el cliente reintenta después
                    }));

                // Login: ventana deslizante por IP. Con ventana fija se podrían hacer 10 intentos a las 10:00:59 y
                // otros 10 a las 10:01:00; la deslizante (6 tramos de 10 s) cuenta siempre el último minuto.
                limiter.AddPolicy(Login, context =>
                    RateLimitPartition.GetSlidingWindowLimiter(IpKey(context), _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = value.LoginAttemptsPerMinute,
                        Window = LoginWindow,
                        SegmentsPerWindow = LoginWindowSegments,
                        QueueLimit = 0
                    }));
            });

        return services;
    }

    // Cada usuario tiene su cubeta, la misma venga con cookie, JWT propio o Keycloak (el nombre es el email).
    // Los anónimos se agrupan por IP.
    private static string ClientKey(HttpContext context) =>
        context.User.Identity is { IsAuthenticated: true, Name: { } name } ? $"user:{name}" : IpKey(context);

    // Detrás de un proxy o balanceador, RemoteIpAddress es la del proxy y todos compartirían la cubeta. Ahí se
    // usa UseForwardedHeaders con la lista de proxies de confianza (KnownProxies), nunca X-Forwarded-For a ciegas:
    // cualquiera puede escribir ese header y elegir su propia cubeta.
    private static string IpKey(HttpContext context) => $"ip:{context.Connection.RemoteIpAddress}";

    private static async ValueTask WriteTooManyRequestsAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        HttpContext http = context.HttpContext;

        // Retry-After: cuántos segundos esperar. Un cliente bien hecho lo respeta en vez de reintentar en bucle.
        // La cubeta de fichas lo calcula; la ventana deslizante (login) no lo informa, y ahí se sugiere un tramo:
        // es una pista prudente, porque si al reintentar sigue sin cupo, recibe otro 429 con otra pista.
        TimeSpan retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan calculated)
            ? calculated
            : LoginWindow / LoginWindowSegments;
        http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RateLimitingSetup))
            .LogWarning("Rate limit: {Method} {Path} rechazado para {Client}", http.Request.Method, http.Request.Path, ClientKey(http));

        await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails =
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Demasiadas peticiones",
                Detail = "Superaste el límite de peticiones. Espera los segundos que indica Retry-After y vuelve a intentar."
            }
        });
    }
}
