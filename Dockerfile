FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ProductOrderApi.csproj ./
RUN dotnet restore ProductOrderApi.csproj

COPY . .
RUN dotnet publish ProductOrderApi.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080
COPY --from=build /app/publish .

USER $APP_UID
ENTRYPOINT ["dotnet", "ProductOrderApi.dll"]
