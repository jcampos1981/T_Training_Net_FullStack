# El panel Blazor del equipo de soporte. Se construye desde la raíz del repositorio:
#   docker build -f src/HelpDesk.Admin/Dockerfile -t helpdesk-admin .

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/HelpDesk.ServiceDefaults/HelpDesk.ServiceDefaults.csproj src/HelpDesk.ServiceDefaults/
COPY src/HelpDesk.Admin/HelpDesk.Admin.csproj src/HelpDesk.Admin/
RUN dotnet restore src/HelpDesk.Admin/HelpDesk.Admin.csproj -r linux-x64

COPY src/HelpDesk.ServiceDefaults/ src/HelpDesk.ServiceDefaults/
COPY src/HelpDesk.Admin/ src/HelpDesk.Admin/
RUN dotnet publish src/HelpDesk.Admin/HelpDesk.Admin.csproj -c Release -r linux-x64 --self-contained false --no-restore -o /app -p:UseAppHost=false
RUN mkdir /keys

# -extra: el panel muestra fechas y números con formato de Chile (es-CL), y eso necesita ICU.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS admin
WORKDIR /app
COPY --from=build /app .
COPY --from=build --chown=1654:1654 /keys /keys
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 CMD ["dotnet", "HelpDesk.Admin.dll", "--health-probe"]
ENTRYPOINT ["dotnet", "HelpDesk.Admin.dll"]
