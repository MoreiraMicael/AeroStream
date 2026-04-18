# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Copy and restore as distinct layers
COPY AeroStream.sln .
COPY src/AeroStream.Ingestion/AeroStream.Ingestion.csproj ./src/AeroStream.Ingestion/
COPY tests/AeroStream.Tests/AeroStream.Tests.csproj ./tests/AeroStream.Tests/
RUN dotnet restore

# Copy everything else and build
COPY . .
RUN dotnet publish src/AeroStream.Ingestion/AeroStream.Ingestion.csproj -c Release -o /out

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Ensure the Kerberos GSSAPI library is available for Npgsql/PG clients
RUN apt-get update \
	&& apt-get install -y --no-install-recommends libgssapi-krb5-2 \
	&& rm -rf /var/lib/apt/lists/*

COPY --from=build /out .
ENTRYPOINT ["dotnet", "AeroStream.Ingestion.dll"]