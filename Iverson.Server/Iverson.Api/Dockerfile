# Pinned by digest (CSR round-3 finding #13): a floating tag lets the base image
# change under this build with no review — the digest is the manifest-list digest
# for mcr.microsoft.com/dotnet/sdk:10.0 as of 2026-10-06 (confirmed via a direct
# `GET /v2/dotnet/sdk/manifests/10.0` against mcr.microsoft.com, Docker-Content-Digest
# response header — same value independently returned by `docker inspect
# --format='{{index .RepoDigests 0}}'` after a fresh pull). Bump this deliberately
# (re-resolve both the tag and its current digest) when a newer 10.0 patch is wanted;
# the tag is kept alongside the digest for human readability only — the digest is
# what's actually resolved.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src

COPY ["Iverson.Server/Iverson.Api/Iverson.Api.csproj",                                               "Iverson.Server/Iverson.Api/"]
COPY ["Iverson.Server/Iverson.Sql/Iverson.Sql.csproj",                                               "Iverson.Server/Iverson.Sql/"]
COPY ["Iverson.Server/Iverson.StarRocks/Iverson.StarRocks.csproj",                                   "Iverson.Server/Iverson.StarRocks/"]
COPY ["Iverson.Server/Iverson.Embeddings/Iverson.Embeddings.csproj",                                 "Iverson.Server/Iverson.Embeddings/"]
COPY ["Iverson.Server/Iverson.Vector/Iverson.Vector.csproj",                                         "Iverson.Server/Iverson.Vector/"]
COPY ["Iverson.Server/Iverson.Patterns/Iverson.Patterns.csproj",                                     "Iverson.Server/Iverson.Patterns/"]
COPY ["Iverson.Server/Iverson.Events/Iverson.Events.csproj",                                         "Iverson.Server/Iverson.Events/"]
COPY ["Iverson.Clients/DotNet/Iverson.Client.Contracts/Iverson.Client.Contracts.csproj",             "Iverson.Clients/DotNet/Iverson.Client.Contracts/"]
RUN dotnet restore "Iverson.Server/Iverson.Api/Iverson.Api.csproj"

# Explicit copies (CSR round-10 #19): only the projects the publish needs reach the build stage.
COPY Iverson.Server/Iverson.Api/ Iverson.Server/Iverson.Api/
COPY Iverson.Server/Iverson.Sql/ Iverson.Server/Iverson.Sql/
COPY Iverson.Server/Iverson.StarRocks/ Iverson.Server/Iverson.StarRocks/
COPY Iverson.Server/Iverson.Embeddings/ Iverson.Server/Iverson.Embeddings/
COPY Iverson.Server/Iverson.Vector/ Iverson.Server/Iverson.Vector/
COPY Iverson.Server/Iverson.Patterns/ Iverson.Server/Iverson.Patterns/
COPY Iverson.Server/Iverson.Events/ Iverson.Server/Iverson.Events/
COPY Iverson.Clients/DotNet/Iverson.Client.Contracts/ Iverson.Clients/DotNet/Iverson.Client.Contracts/
COPY Iverson.Clients/Common/Proto/ Iverson.Clients/Common/Proto/
RUN dotnet publish "Iverson.Server/Iverson.Api/Iverson.Api.csproj" -c Release -o /app/publish --no-restore

# Pinned by digest (CSR round-3 finding #13) — same rationale as the sdk stage above.
# Manifest-list digest for mcr.microsoft.com/dotnet/aspnet:10.0 as of 2026-10-06.
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS runtime
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
