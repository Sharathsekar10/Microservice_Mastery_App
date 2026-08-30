# ---- STAGE 1: build ---------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY NotificationService.csproj .
RUN dotnet restore NotificationService.csproj

COPY . .
RUN dotnet publish NotificationService.csproj -c Release -o /app/publish --no-restore

# ---- STAGE 2: final -----------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

EXPOSE 8080

ENTRYPOINT ["dotnet", "NotificationService.dll"]
