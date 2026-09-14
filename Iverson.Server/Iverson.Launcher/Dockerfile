FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d AS build
WORKDIR /src

COPY ["Iverson.Launcher/Iverson.Launcher.csproj", "Iverson.Launcher/"]
RUN dotnet restore "Iverson.Launcher/Iverson.Launcher.csproj"

COPY . .
RUN dotnet publish "Iverson.Launcher/Iverson.Launcher.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:8a153b5889d796b6450295b383596b13308c24c230515f8a7770ce1b94e0c460 AS runtime
WORKDIR /app

# Docker CLI needed to run compose commands from inside container
RUN apt-get update && apt-get install -y docker.io && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Intentionally NOT switching to a non-root USER here (CSR finding #8,
# docs/criticalreviews/2026-07-16-iverson.server-critical-security-review-1.md).
# This container's whole purpose is driving the Docker CLI against the host's
# /var/run/docker.sock (see Program.cs), which normally requires the caller to
# either be root or be a member of a group matching the socket's host GID.
# That GID varies per host/Docker installation and can't be baked in at image
# build time, so a hardcoded non-root user would make Docker CLI access
# unreliable across environments without buying real isolation (the container
# already needs root-equivalent access to the host's Docker daemon to do its
# job). This image is also not currently built or run by docker-compose, kind,
# or any CI workflow (verified via repo-wide grep) — Iverson.Launcher's
# documented usage is `dotnet run` directly on the host (see README.md), so
# this is a dev-tooling convenience Dockerfile, not part of any deployed path.
# Revisit if this image is ever wired into an actual deployment/CI flow.
ENTRYPOINT ["dotnet", "Iverson.Launcher.dll"]
