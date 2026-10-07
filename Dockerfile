# --- Build stage ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Ritplanning.Core/*.csproj src/Ritplanning.Core/
COPY src/Ritplanning.Web/*.csproj src/Ritplanning.Web/
RUN dotnet restore src/Ritplanning.Web/Ritplanning.Web.csproj
COPY src/ src/
RUN dotnet publish src/Ritplanning.Web/Ritplanning.Web.csproj -c Release -o /app/publish --no-restore

# --- Runtime stage (kleine image, niet-root gebruiker) ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
# $APP_UID is de ingebouwde niet-geprivilegieerde gebruiker in de .NET 8 images
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ritplanning.Web.dll"]
