# Imagen para ECS Express Mode. Puerto del contenedor: 8080. Health check: /health
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ProductosApi.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION=local
ARG REVISION=unknown
ARG SOURCE
LABEL org.opencontainers.image.title="productos-api" \
      org.opencontainers.image.version=$VERSION \
      org.opencontainers.image.revision=$REVISION \
      org.opencontainers.image.source=$SOURCE \
      pe.upc.project="software-cursos" \
      pe.upc.component="backend"
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "ProductosApi.dll"]
