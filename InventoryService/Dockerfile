# ---- STAGE 1: build ---------------------------------------------------
# Full SDK image - compiler, MSBuild, NuGet. Only exists in this stage.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy ONLY the .csproj first, then restore.
# Why: this layer's cache only invalidates when dependencies change.
COPY InventoryService.csproj .
RUN dotnet restore InventoryService.csproj

# Now copy the rest of the source and publish.
COPY . .
RUN dotnet publish InventoryService.csproj -c Release -o /app/publish --no-restore

# ---- STAGE 2: final -----------------------------------------------------
# Runtime-only image. No SDK, no compiler.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Pull ONLY the published output from the build stage.
COPY --from=build /app/publish .

# .NET container images listen on 8080 for HTTP by default.
EXPOSE 8080

# Foreground process, PID 1, no wrapper script.
ENTRYPOINT ["dotnet", "InventoryService.dll"]
