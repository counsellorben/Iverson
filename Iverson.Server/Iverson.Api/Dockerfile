# Pinned by digest (CSR round-3 finding #13): a floating tag lets the base image
# change under this build with no review — the digest is the manifest-list digest
# for mcr.microsoft.com/dotnet/sdk:10.0 as of 2026-09-13 (confirmed via a direct
# `GET /v2/dotnet/sdk/manifests/10.0` against mcr.microsoft.com, Docker-Content-Digest
# response header — same value independently returned by `docker inspect
# --format='{{index .RepoDigests 0}}'` after a fresh pull). Bump this deliberately
# (re-resolve both the tag and its current digest) when a newer 10.0 patch is wanted;
# the tag is kept alongside the digest for human readability only — the digest is
# what's actually resolved.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d AS build
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

# Pinned by digest (CSR round-3 finding #13) — same rationale as the sdk stage above.
# Manifest-list digest for mcr.microsoft.com/dotnet/aspnet:10.0 as of 2026-09-13.
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:6a94333d37514e385650a3c81a55e5350b67253dbe136e9cf17e499c35606a8c AS runtime
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
