# Builds one scenario project (src/Repro.Race or src/Repro.Alias), selected with the PROJECT build arg.
# The build context is the repository root (see compose.yaml), so the Directory.*.props files are available.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/${PROJECT}/${PROJECT}.csproj src/${PROJECT}/
RUN dotnet restore src/${PROJECT}/${PROJECT}.csproj
COPY src/Shared/ src/Shared/
COPY src/${PROJECT}/ src/${PROJECT}/
# A fixed assembly name lets one ENTRYPOINT serve both projects.
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release -o /app --no-restore -p:AssemblyName=repro

FROM mcr.microsoft.com/dotnet/runtime:10.0
# Npgsql probes for GSSAPI at connection time; without this library it prints a load error to stderr.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "repro.dll"]
