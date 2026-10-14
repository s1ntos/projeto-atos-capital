# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# restaura primeiro para aproveitar cache de camadas
COPY backend.csproj ./
RUN dotnet restore backend.csproj

COPY . ./
RUN dotnet publish backend.csproj -c Release -o /app/publish --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    Anexos__Diretorio=/app/uploads

# diretório persistente de anexos (montar volume em /app/uploads no orquestrador)
RUN mkdir -p /app/uploads && chown -R app:app /app
USER app

COPY --from=build /app/publish ./

EXPOSE 8080
ENTRYPOINT ["dotnet", "backend.dll"]
