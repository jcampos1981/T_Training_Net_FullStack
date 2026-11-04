using Aspire.Hosting.JavaScript;
using HelpDesk.AppHost;

// Toda la aplicación en desarrollo, con un solo comando. Se describe como código lo que hay y de qué depende cada cosa, y Aspire la levanta en
// orden, le pasa a cada una sus direcciones, y muestra todo en un dashboard. Es para desarrollo: en producción están el chart o las plantillas.
IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// Los mismos secretos que Compose: nada que configurar aparte.
builder.Configuration.AddEnvFile("../../deploy/.env");

// Cada secreto se declara una sola vez, aunque lo usen varias aplicaciones.
Dictionary<string, IResourceBuilder<ParameterResource>> secrets = [];

IResourceBuilder<ParameterResource> Secret(string variable)
{
    string name = EnvFile.ParameterNameOf(variable);

    if (!secrets.TryGetValue(name, out IResourceBuilder<ParameterResource>? secret))
    {
        secret = builder.AddParameter(name, secret: true);
        secrets[name] = secret;
    }

    return secret;
}

// ---- Infraestructura ----

// El mismo volumen de Compose: los mismos datos de desarrollo. Por eso no pueden correr los dos a la vez.
IResourceBuilder<SqlServerDatabaseResource> database = builder
    .AddSqlServer("sql", Secret("MSSQL_SA_PASSWORD"), port: 1433)
    .WithImageTag("2025-latest")
    .WithDataVolume("helpdesk_sqlserver-data")
    .AddDatabase("HelpDesk");

IResourceBuilder<RedisResource> redis = builder.AddRedis("Redis", port: 6379);

IResourceBuilder<RabbitMQServerResource> rabbitMq = builder
    .AddRabbitMQ("RabbitMq", password: Secret("RABBITMQ_PASSWORD"), port: 5672)
    .WithManagementPlugin(port: 15672);

// Keycloak no tiene una integración estable de Aspire: es un contenedor como cualquier otro. En el 8080, como siempre: su dirección es el "iss"
// de los tokens, y la lista de direcciones permitidas del realm depende de los puertos de las aplicaciones.
IResourceBuilder<ContainerResource> keycloak = builder
    .AddContainer("keycloak", "quay.io/keycloak/keycloak", "26.8.1")
    .WithArgs("start-dev", "--import-realm")
    .WithBindMount("../../deploy/keycloak", "/opt/keycloak/data/import", isReadOnly: true)
    .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http")
    .WithHttpEndpoint(targetPort: 9000, name: "management")
    .WithHttpHealthCheck("/health/ready", endpointName: "management")
    .WithEnvironment("KC_HOSTNAME", "http://localhost:8080")
    .WithEnvironment("KC_HEALTH_ENABLED", "true")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", Secret("KEYCLOAK_ADMIN_PASSWORD"))
    .WithEnvironment("HELPDESK_DEV_PASSWORD", Secret("HELPDESK_DEV_PASSWORD"))
    .WithEnvironment("HELPDESK_BFF_CLIENT_SECRET", Secret("HELPDESK_BFF_CLIENT_SECRET"))
    .WithEnvironment("HELPDESK_ADMIN_CLIENT_SECRET", Secret("HELPDESK_ADMIN_CLIENT_SECRET"));

// Un modelo local, para la respuesta sugerida. Comparte el volumen de Compose: los modelos no se vuelven a bajar.
IResourceBuilder<ContainerResource> ollama = builder
    .AddContainer("ollama", "ollama/ollama", "0.40.14")
    .WithHttpEndpoint(port: 11434, targetPort: 11434, name: "http")
    .WithHttpHealthCheck("/", endpointName: "http")
    .WithEnvironment("OLLAMA_KEEP_ALIVE", "1h")
    .WithVolume("helpdesk_ollama-data", "/root/.ollama");

IResourceBuilder<ContainerResource> models = builder
    .AddContainer("ollama-models", "ollama/ollama", "0.40.14")
    .WithEntrypoint("/bin/sh")
    .WithArgs("-c", "for model in paraphrase-multilingual gemma3:4b; do ollama show \"$model\" > /dev/null 2>&1 || ollama pull \"$model\" || exit 1; done")
    .WithEnvironment("OLLAMA_HOST", ollama.GetEndpoint("http"))
    .WaitFor(ollama);

const string KeycloakAuthority = "http://localhost:8080/realms/helpdesk";

// ---- La aplicación ----

// La imagen de producción: corre y termina, y la API espera a que termine.
IResourceBuilder<ContainerResource> migrator = builder
    .AddDockerfile("migrator", "../..", "src/HelpDesk.Api/Dockerfile", "migrator")
    .WithEnvironment("ConnectionStrings__HelpDesk", database)
    .WaitFor(database);

IResourceBuilder<ProjectResource> api = builder.AddProject<Projects.HelpDesk_Api>("api", launchProfileName: "http")
    .WithReference(database)
    .WithReference(redis)
    .WithReference(rabbitMq)
    .WithEnvironment("Keycloak__Authority", KeycloakAuthority)
    .WithEnvironment("Ai__Enabled", "true")
    .WithEnvironment("Ai__OllamaEndpoint", ollama.GetEndpoint("http"))
    .WithHttpHealthCheck("/health/ready")
    .WaitForCompletion(migrator)
    .WaitForCompletion(models)
    .WaitFor(redis)
    .WaitFor(rabbitMq)
    .WaitFor(keycloak);

builder.AddProject<Projects.HelpDesk_Worker>("worker")
    .WithReference(database)
    .WithReference(rabbitMq)
    .WithEnvironment("Ai__Enabled", "true")
    .WithEnvironment("Ai__OllamaEndpoint", ollama.GetEndpoint("http"))
    .WithHttpHealthCheck("/health/ready")
    .WaitForCompletion(migrator)
    .WaitForCompletion(models)
    .WaitFor(rabbitMq);

// El portal se compila y lo sirve el BFF, como en producción. Sin el servidor de Vite: Keycloak solo acepta su puerto 5173, que puede estar ocupado.
IResourceBuilder<JavaScriptAppResource> portal = builder.AddJavaScriptApp("portal", "../HelpDesk.Portal", "build");

builder.AddProject<Projects.HelpDesk_Bff>("bff", launchProfileName: "https")
    .WithEnvironment("Keycloak__Authority", KeycloakAuthority)
    .WithEnvironment("Keycloak__ClientSecret", Secret("HELPDESK_BFF_CLIENT_SECRET"))
    .WithEnvironment("ReverseProxy__Clusters__helpdesk-api__Destinations__api__Address", api.GetEndpoint("http"))
    .WithHttpHealthCheck("/health/ready", endpointName: "https")
    .WaitForCompletion(portal)
    .WaitFor(api);

builder.AddProject<Projects.HelpDesk_Admin>("admin", launchProfileName: "https")
    .WithEnvironment("Keycloak__Authority", KeycloakAuthority)
    .WithEnvironment("Keycloak__ClientSecret", Secret("HELPDESK_ADMIN_CLIENT_SECRET"))
    .WithEnvironment("HelpDeskApi__BaseUrl", api.GetEndpoint("http"))
    .WithEnvironment("Suggestions__Enabled", "true")
    .WithHttpHealthCheck("/health/ready", endpointName: "https")
    .WaitFor(api);

builder.Build().Run();
