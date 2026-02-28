FROM mcr.microsoft.com/playwright/dotnet:v1.58.0-noble AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["OH-Medina/OH-Medina.csproj", "OH-Medina/"]
RUN dotnet restore "OH-Medina/OH-Medina.csproj"
COPY . .
WORKDIR "/src/OH-Medina"
RUN dotnet build "OH-Medina.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "OH-Medina.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "OH-Medina.dll"]
