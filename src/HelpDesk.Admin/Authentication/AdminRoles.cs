namespace HelpDesk.Admin.Authentication;

public static class AdminRoles
{
    public const string Agent = "Agent";
    public const string Supervisor = "Supervisor";
    public const string Admin = "Admin";

    // Para <AuthorizeView Roles="...">: quienes ven y mueven el trabajo de todo el equipo.
    public const string Supervision = $"{Supervisor},{Admin}";
}
