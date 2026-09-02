# syntax=docker/dockerfile:1.7

FROM node:26.7.0-bookworm-slim AS client-build
WORKDIR /src/Workbench.Client
COPY src/Workbench.Client/package.json src/Workbench.Client/package-lock.json ./
RUN npm ci
COPY src/Workbench.Client/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.400-noble AS server-build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Workbench.Server/Workbench.Server.csproj src/Workbench.Server/packages.lock.json ./src/Workbench.Server/
COPY src/Workbench.Database/Workbench.Database.csproj src/Workbench.Database/packages.lock.json ./src/Workbench.Database/
RUN dotnet restore src/Workbench.Server/Workbench.Server.csproj --locked-mode
RUN dotnet restore src/Workbench.Database/Workbench.Database.csproj --locked-mode
COPY src/Workbench.Server/ ./src/Workbench.Server/
COPY src/Workbench.Database/ ./src/Workbench.Database/
COPY --from=client-build /src/Workbench.Client/dist ./src/Workbench.Client/dist
RUN dotnet publish src/Workbench.Server/Workbench.Server.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:BuildClient=false \
    -p:UseAppHost=false
RUN dotnet publish src/Workbench.Database/Workbench.Database.csproj \
    --configuration Release \
    --no-restore \
    --output /database/publish \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-noble-chiseled-extra AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    WORKBENCH_HEALTH_URL=http://127.0.0.1:8080/health/ready
COPY --from=server-build --chown=1654:1654 /app/publish/ ./
COPY --from=server-build --chown=1654:1654 /database/publish/ /opt/workbench/database/
USER 1654
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=6s --start-period=10s --retries=3 \
    CMD ["dotnet", "Workbench.Server.dll", "--health-check"]
ENTRYPOINT ["dotnet", "Workbench.Server.dll"]
