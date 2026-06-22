FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Iverson.Launcher/Iverson.Launcher.csproj", "Iverson.Launcher/"]
RUN dotnet restore "Iverson.Launcher/Iverson.Launcher.csproj"

COPY . .
RUN dotnet publish "Iverson.Launcher/Iverson.Launcher.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

# Docker CLI needed to run compose commands from inside container
RUN apt-get update && apt-get install -y docker.io && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Iverson.Launcher.dll"]
