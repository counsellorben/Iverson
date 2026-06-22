FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Iverson.Api/Iverson.Api.csproj", "Iverson.Api/"]
COPY ["Iverson.Sql/Iverson.Sql.csproj", "Iverson.Sql/"]
COPY ["Iverson.Elasticsearch/Iverson.Elasticsearch.csproj", "Iverson.Elasticsearch/"]
COPY ["Iverson.Vector/Iverson.Vector.csproj", "Iverson.Vector/"]
COPY ["Iverson.Events/Iverson.Events.csproj", "Iverson.Events/"]
RUN dotnet restore "Iverson.Api/Iverson.Api.csproj"

COPY . .
RUN dotnet publish "Iverson.Api/Iverson.Api.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Iverson.Api.dll"]
