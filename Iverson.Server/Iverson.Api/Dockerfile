FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Iverson.Server/Iverson.Api/Iverson.Api.csproj",                                               "Iverson.Server/Iverson.Api/"]
COPY ["Iverson.Server/Iverson.Sql/Iverson.Sql.csproj",                                               "Iverson.Server/Iverson.Sql/"]
COPY ["Iverson.Server/Iverson.StarRocks/Iverson.StarRocks.csproj",                                   "Iverson.Server/Iverson.StarRocks/"]
COPY ["Iverson.Server/Iverson.Embeddings/Iverson.Embeddings.csproj",                                 "Iverson.Server/Iverson.Embeddings/"]
COPY ["Iverson.Server/Iverson.Vector/Iverson.Vector.csproj",                                         "Iverson.Server/Iverson.Vector/"]
COPY ["Iverson.Server/Iverson.Events/Iverson.Events.csproj",                                         "Iverson.Server/Iverson.Events/"]
COPY ["Iverson.Clients/DotNet/Iverson.Client.Contracts/Iverson.Client.Contracts.csproj",             "Iverson.Clients/DotNet/Iverson.Client.Contracts/"]
RUN dotnet restore "Iverson.Server/Iverson.Api/Iverson.Api.csproj"

COPY . .
RUN dotnet publish "Iverson.Server/Iverson.Api/Iverson.Api.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# mcr.microsoft.com/dotnet/aspnet:10.0 already ships a built-in non-root
# "ubuntu" user at uid=1000/gid=1000 (verified via `docker run ... id ubuntu`),
# which matches the Helm chart's securityContext (runAsUser/runAsGroup: 1000)
# exactly, so we reuse it rather than creating a new user. This gives parity
# between docker-compose / ad-hoc `docker run` and the kind/cloud deployment
# target.
COPY --from=build --chown=1000:1000 /app/publish .

USER ubuntu
ENTRYPOINT ["dotnet", "Iverson.Api.dll"]
