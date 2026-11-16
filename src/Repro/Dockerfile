# Build context is the repository root (see compose.yaml), so the Directory.*.props files are available.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Repro/Repro.csproj src/Repro/
RUN dotnet restore src/Repro/Repro.csproj
COPY src/Repro/ src/Repro/
RUN dotnet publish src/Repro/Repro.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
# Npgsql probes for GSSAPI at connection time; without this library it prints a load error to stderr.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Repro.dll"]
