namespace HelpDesk.Api.Features.Accounts;

// GET /api/me: quién soy según la API. Sirve para depurar y para que un frontend sepa qué mostrar.
public sealed record CurrentUserResponse(string UserName, string? Agent, IReadOnlyList<string> Roles);
