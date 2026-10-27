using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HelpDesk.Api.Features.Accounts.Keycloak;

// Tokens emitidos por Keycloak (OpenID Connect). La API no ve contraseñas ni emite nada: solo valida.
public static class KeycloakAuthentication
{
    public const string SchemeName = "Keycloak";

    public static AuthenticationBuilder AddKeycloakBearer(this AuthenticationBuilder builder)
    {
        builder.Services.AddOptions<KeycloakOptions>()
            .BindConfiguration(KeycloakOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Es el mismo JwtBearer que valida nuestros JWT, registrado otra vez con otro nombre y otra configuración.
        builder.AddJwtBearer(SchemeName);

        builder.Services.AddOptions<JwtBearerOptions>(SchemeName)
            .Configure<IOptions<KeycloakOptions>, IHostEnvironment>((bearer, keycloakOptions, environment) =>
            {
                KeycloakOptions keycloak = keycloakOptions.Value;

                // Con Authority, JwtBearer pide {Authority}/.well-known/openid-configuration en el primer uso, y de ahí
                // saca el emisor válido y las llaves PÚBLICAS (jwks_uri). Las guarda en memoria, las refresca cada
                // 24 horas, y antes si llega un token firmado con una llave que no conoce (Keycloak rotó sus llaves).
                bearer.Authority = keycloak.Authority;

                // Keycloak de desarrollo corre en http. En cualquier otro ambiente se exige https: si alguien
                // intercepta el discovery document, puede cambiar las llaves y la API aceptaría tokens falsos.
                bearer.RequireHttpsMetadata = !environment.IsDevelopment();

                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidAudience = keycloak.Audience,
                    // Solo RS256: llave privada para firmar (solo Keycloak la tiene) y pública para validar.
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    // Igual que con nuestros JWT: el nombre es el email, y los roles los agrega KeycloakClaims.
                    NameClaimType = ClaimTypes.Email,
                    RoleClaimType = ClaimTypes.Role
                };

                bearer.Events = new JwtBearerEvents
                {
                    // Se ejecuta solo cuando firma, emisor, audiencia y vencimiento ya están validados.
                    OnTokenValidated = context =>
                    {
                        if (context.Principal?.Identity is ClaimsIdentity identity)
                        {
                            KeycloakClaims.AddHelpDeskClaims(identity);
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        return builder;
    }
}
