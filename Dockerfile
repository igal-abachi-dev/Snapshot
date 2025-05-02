# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

# Copy and restore all projects (handles multi-csproj solutions too)
COPY *.sln ./
COPY SnapshotWebApi/*.csproj ./SnapshotWebApi/
RUN dotnet restore

# Copy the full source tree
COPY . ./

# Build and publish release
RUN dotnet publish SnapshotWebApi.csproj -c Release -o /app/out

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app/out ./

# Set environment port for Render
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "SnapshotWebApi.dll"]
