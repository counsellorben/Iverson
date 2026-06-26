FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Iverson.Server/Iverson.Api/Iverson.Api.csproj",                                               "Iverson.Server/Iverson.Api/"]
COPY ["Iverson.Server/Iverson.Sql/Iverson.Sql.csproj",                                               "Iverson.Server/Iverson.Sql/"]
COPY ["Iverson.Server/Iverson.StarRocks/Iverson.StarRocks.csproj",                                   "Iverson.Server/Iverson.StarRocks/"]
COPY ["Iverson.Server/Iverson.Embeddings/Iverson.Embeddings.csproj",                                 "Iverson.Server/Iverson.Embeddings/"]
COPY ["Iverson.Server/Iverson.Vector/Iverson.Vector.csproj",                                         "Iverson.Server/Iverson.Vector/"]
COPY ["Iverson.Server/Iverson.Events/Iverson.Events.csproj",                                         "Iverson.Server/Iverson.Events/"]
COPY ["Iverson.Client/Iverson.Client.Contracts/Iverson.Client.Contracts.csproj",                     "Iverson.Client/Iverson.Client.Contracts/"]
RUN dotnet restore "Iverson.Server/Iverson.Api/Iverson.Api.csproj"

COPY . .
RUN dotnet publish "Iverson.Server/Iverson.Api/Iverson.Api.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Iverson.Api.dll"]
