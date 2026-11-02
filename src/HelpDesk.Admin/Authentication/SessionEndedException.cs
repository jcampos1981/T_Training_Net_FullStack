namespace HelpDesk.Admin.Authentication;

// La sesión ya no sirve para llamar a la API. ApiErrorBoundary la atrapa y recarga la página: esa petición HTTP
// sí pasa por la cookie, que la rechaza y lleva al usuario a Keycloak.
public sealed class SessionEndedException() : Exception("La sesión terminó.");
