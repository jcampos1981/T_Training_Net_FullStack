namespace HelpDesk.Api.Features.Accounts.Tokens;

// Los mismos nombres que usa OAuth 2.0 (token_type, access_token, expires_in, refresh_token), en camelCase.
public sealed record TokenResponse(string TokenType, string AccessToken, int ExpiresIn, string RefreshToken);
