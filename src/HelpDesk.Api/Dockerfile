# La API y, con --target migrator, el programa que aplica las migraciones. Se construye desde la raíz del repositorio:
#   docker build -f src/HelpDesk.Api/Dockerfile -t helpdesk-api .
#   docker build -f src/HelpDesk.Api/Dockerfile --target migrator -t helpdesk-migrator .

# ---- Compilar: con el SDK completo, que no llega a la imagen final ----
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /repo

# Primero solo lo que define las dependencias: mientras no cambie, Docker reutiliza esta capa y no vuelve a descargar los paquetes.
COPY global.json Directory.Build.props Directory.Packages.props dotnet-tools.json ./
COPY src/HelpDesk.Domain/HelpDesk.Domain.csproj src/HelpDesk.Domain/
COPY src/HelpDesk.ServiceDefaults/HelpDesk.ServiceDefaults.csproj src/HelpDesk.ServiceDefaults/
COPY src/HelpDesk.Api/HelpDesk.Api.csproj src/HelpDesk.Api/
RUN dotnet restore src/HelpDesk.Api/HelpDesk.Api.csproj -r linux-x64

COPY src/HelpDesk.Domain/ src/HelpDesk.Domain/
COPY src/HelpDesk.ServiceDefaults/ src/HelpDesk.ServiceDefaults/
COPY src/HelpDesk.Api/ src/HelpDesk.Api/
# -r linux-x64 deja afuera las librerías nativas de otros sistemas. No es self-contained: el runtime lo pone la imagen base.
RUN dotnet publish src/HelpDesk.Api/HelpDesk.Api.csproj -c Release -r linux-x64 --self-contained false --no-restore -o /app -p:UseAppHost=false

# Una imagen chiseled no tiene shell para crear la carpeta después. Un volumen nuevo hereda el dueño de la carpeta que tapa.
RUN mkdir /keys

# ---- Migraciones: un ejecutable que no necesita el SDK ----
FROM build AS bundle
# dotnet ef arranca el Program de la API para encontrar el DbContext, y el Program exige una cadena de conexión. Esta no se usa ni queda
# en la imagen: al ejecutarse, el programa lee la verdadera de la variable de entorno.
RUN dotnet tool restore \
    && ConnectionStrings__HelpDesk="Server=solo-para-compilar" dotnet ef migrations bundle --project src/HelpDesk.Api --configuration Release --self-contained -r linux-x64 -o /out/efbundle

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled-extra AS migrator
COPY --from=bundle /out/efbundle /efbundle
ENTRYPOINT ["/efbundle"]

# ---- La API: al final para que sea lo que se construye por defecto ----
# chiseled: Ubuntu reducido a lo que .NET necesita, sin shell ni gestor de paquetes, y con usuario no root (app, UID 1654) y puerto
# 8080 por defecto. -extra agrega ICU: sin esas librerías, .NET corre en modo invariante y SqlClient no lo admite.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS api
WORKDIR /app
COPY --from=build /app .
COPY --from=build --chown=1654:1654 /keys /keys
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 CMD ["dotnet", "HelpDesk.Api.dll", "--health-probe"]
ENTRYPOINT ["dotnet", "HelpDesk.Api.dll"]
