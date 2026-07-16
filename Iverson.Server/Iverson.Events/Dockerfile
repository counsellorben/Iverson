# Iverson.Events is a class library — it is packaged into Iverson.Api at build time.
# This Dockerfile builds just the library for standalone artifact inspection or packaging.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Iverson.Events/Iverson.Events.csproj", "Iverson.Events/"]
RUN dotnet restore "Iverson.Events/Iverson.Events.csproj"

COPY Iverson.Events/ Iverson.Events/

# Non-root user for the build/publish stages (defense-in-depth / lint convention —
# see CSR finding #8). The final stage below is `FROM scratch`, which has no OS or
# user model, so this has no effect on anything that actually runs; it's here for
# static Dockerfile-lint conventions (e.g. hadolint DL3002) and consistency with the
# other first-party Dockerfiles, not because it closes any real attack surface.
# mcr.microsoft.com/dotnet/sdk:10.0 already ships a built-in non-root "ubuntu" user
# at uid=1000/gid=1000, so we reuse it instead of creating a new one.
RUN mkdir -p /app/build && chown -R 1000:1000 /src /app
USER ubuntu

RUN dotnet build "Iverson.Events/Iverson.Events.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet pack "Iverson.Events/Iverson.Events.csproj" -c Release -o /app/packages

FROM scratch AS export
COPY --from=publish /app/packages /packages
