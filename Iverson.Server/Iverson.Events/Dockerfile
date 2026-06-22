# Iverson.Events is a class library — it is packaged into Iverson.Api at build time.
# This Dockerfile builds just the library for standalone artifact inspection or packaging.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Iverson.Events/Iverson.Events.csproj", "Iverson.Events/"]
RUN dotnet restore "Iverson.Events/Iverson.Events.csproj"

COPY Iverson.Events/ Iverson.Events/
RUN dotnet build "Iverson.Events/Iverson.Events.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet pack "Iverson.Events/Iverson.Events.csproj" -c Release -o /app/packages

FROM scratch AS export
COPY --from=publish /app/packages /packages
