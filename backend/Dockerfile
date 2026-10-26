# The ASP.NET Core API. Build context: the repository root (docker build -f backend/Dockerfile .).
# Stage 1 compiles with the .NET SDK; the last stage ("runtime") contains only the ASP.NET runtime and the published files.
# The "migrate" stage is a separate small image used by compose.yaml (build it with --target migrate).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from the project files only, so the NuGet layer is cached until a dependency changes.
COPY backend/controllers/TechStore.Api.csproj backend/controllers/
COPY backend/services/TechStore.Services.csproj backend/services/
COPY backend/auth/TechStore.Auth.csproj backend/auth/
COPY backend/database/TechStore.Database.csproj backend/database/
COPY backend/dto/TechStore.Dto.csproj backend/dto/
RUN dotnet restore backend/controllers/TechStore.Api.csproj

COPY backend/ backend/
RUN dotnet publish backend/controllers/TechStore.Api.csproj -c Release --no-restore -o /app/publish

# ---- Database migrations -------------------------------------------------------------------------------------------
# Stage 2a turns the EF Core migrations into ONE SQL script. --idempotent makes the script check the __EFMigrationsHistory
# table and run only the migrations that have not been applied yet, so running it again changes nothing and never drops data.
FROM build AS migration-script
RUN dotnet tool install --global dotnet-ef --version 10.0.14
ENV PATH="$PATH:/root/.dotnet/tools"
RUN dotnet ef migrations script --idempotent --project backend/database --output /migrate.sql

# Stage 2b is the one-shot "migrate" container: psql, that script, and the script that creates the limited role the API uses.
# Connection settings (PGHOST, PGUSER, PGPASSWORD, PGDATABASE) and TECHSTORE_APP_DB_PASSWORD are supplied at run time.
# ON_ERROR_STOP makes any failure a non-zero exit, so the API never starts on a half-migrated database.
FROM postgres:17-alpine AS migrate
COPY --from=migration-script /migrate.sql /migrate/migrate.sql
COPY backend/database/Provisioning/app-role.sql backend/database/Provisioning/run-provisioning.psql /migrate/
USER postgres
ENTRYPOINT ["psql", "--no-psqlrc", "-v", "ON_ERROR_STOP=1", "-f", "/migrate/migrate.sql", "-f", "/migrate/run-provisioning.psql"]

# ---- The API image ----------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# The key ring (cookie and CSRF protection keys) lives in a folder meant to be a volume, so sessions survive a restart.
# Creating it owned by the non-root user means a new volume mounted here is writable by that user.
RUN mkdir /keys && chown app:app /keys
ENV DataProtection__KeysPath=/keys \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production

# No passwords or connection strings here: they are supplied at run time (ConnectionStrings__TechStore, Auth__InitialAdmin__*).
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "TechStore.Api.dll"]
