# El worker que consume los eventos. Se construye desde la raíz del repositorio:
#   docker build -f src/HelpDesk.Worker/Dockerfile -t helpdesk-worker .

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/HelpDesk.Ai/HelpDesk.Ai.csproj src/HelpDesk.Ai/
COPY src/HelpDesk.Messaging/HelpDesk.Messaging.csproj src/HelpDesk.Messaging/
COPY src/HelpDesk.ServiceDefaults/HelpDesk.ServiceDefaults.csproj src/HelpDesk.ServiceDefaults/
COPY src/HelpDesk.Worker/HelpDesk.Worker.csproj src/HelpDesk.Worker/
RUN dotnet restore src/HelpDesk.Worker/HelpDesk.Worker.csproj -r linux-x64

COPY src/HelpDesk.Ai/ src/HelpDesk.Ai/
COPY src/HelpDesk.Messaging/ src/HelpDesk.Messaging/
COPY src/HelpDesk.ServiceDefaults/ src/HelpDesk.ServiceDefaults/
COPY src/HelpDesk.Worker/ src/HelpDesk.Worker/
RUN dotnet publish src/HelpDesk.Worker/HelpDesk.Worker.csproj -c Release -r linux-x64 --self-contained false --no-restore -o /app -p:UseAppHost=false

# -extra: SqlClient no admite el modo invariante, y sin ICU .NET corre en él.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS worker
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 CMD ["dotnet", "HelpDesk.Worker.dll", "--health-probe"]
ENTRYPOINT ["dotnet", "HelpDesk.Worker.dll"]
