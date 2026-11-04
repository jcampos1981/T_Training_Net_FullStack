# El BFF, con el portal React adentro: los dos son un solo origen, sin CORS. Se construye desde la raíz del repositorio:
#   docker build -f src/HelpDesk.Bff/Dockerfile -t helpdesk-bff .

# ---- Compilar el portal ----
FROM node:24-alpine AS portal
WORKDIR /repo/src/HelpDesk.Portal
COPY src/HelpDesk.Portal/package.json src/HelpDesk.Portal/package-lock.json src/HelpDesk.Portal/.npmrc ./
RUN npm ci
COPY src/HelpDesk.Portal/ ./
# vite.config.ts deja el resultado en ../HelpDesk.Bff/wwwroot.
RUN npm run build

# ---- Compilar el BFF ----
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/HelpDesk.ServiceDefaults/HelpDesk.ServiceDefaults.csproj src/HelpDesk.ServiceDefaults/
COPY src/HelpDesk.Bff/HelpDesk.Bff.csproj src/HelpDesk.Bff/
RUN dotnet restore src/HelpDesk.Bff/HelpDesk.Bff.csproj -r linux-x64

COPY src/HelpDesk.ServiceDefaults/ src/HelpDesk.ServiceDefaults/
COPY src/HelpDesk.Bff/ src/HelpDesk.Bff/
COPY --from=portal /repo/src/HelpDesk.Bff/wwwroot src/HelpDesk.Bff/wwwroot
RUN dotnet publish src/HelpDesk.Bff/HelpDesk.Bff.csproj -c Release -r linux-x64 --self-contained false --no-restore -o /app -p:UseAppHost=false
RUN mkdir /keys

# ---- La imagen final ----
# Sin -extra: el BFF no formatea fechas ni habla con SQL Server, así que el modo invariante le basta y la imagen es más chica.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS bff
WORKDIR /app
COPY --from=build /app .
COPY --from=build --chown=1654:1654 /keys /keys
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 CMD ["dotnet", "HelpDesk.Bff.dll", "--health-probe"]
ENTRYPOINT ["dotnet", "HelpDesk.Bff.dll"]
