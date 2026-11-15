FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Stars.slnx dotnet-tools.json ./
COPY src/Stars.Api/Stars.Api.csproj src/Stars.Api/
COPY src/Stars.Application/Stars.Application.csproj src/Stars.Application/
COPY src/Stars.Domain/Stars.Domain.csproj src/Stars.Domain/
COPY src/Stars.Infrastructure/Stars.Infrastructure.csproj src/Stars.Infrastructure/
COPY src/Stars.Connectors.Abstractions/Stars.Connectors.Abstractions.csproj src/Stars.Connectors.Abstractions/
COPY src/Stars.Connectors.Rest/Stars.Connectors.Rest.csproj src/Stars.Connectors.Rest/
COPY src/Stars.Connectors.Eva/Stars.Connectors.Eva.csproj src/Stars.Connectors.Eva/
COPY tests/Stars.Tests/Stars.Tests.csproj tests/Stars.Tests/
RUN dotnet restore Stars.slnx

COPY . .

FROM node:22-alpine AS frontend
WORKDIR /ui
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM build AS publish
COPY --from=frontend /src/Stars.Api/wwwroot ./src/Stars.Api/wwwroot
RUN dotnet publish src/Stars.Api/Stars.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=publish /app/publish ./
COPY fixtures ./fixtures
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5080 5081
ENTRYPOINT ["dotnet", "Stars.Api.dll"]
