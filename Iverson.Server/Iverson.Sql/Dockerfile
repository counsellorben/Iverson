# Iverson.Sql is a class library — it is packaged into Iverson.Api at build time.
# This Dockerfile builds just the library for standalone artifact inspection or packaging.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Iverson.Sql/Iverson.Sql.csproj", "Iverson.Sql/"]
RUN dotnet restore "Iverson.Sql/Iverson.Sql.csproj"

COPY Iverson.Sql/ Iverson.Sql/
RUN dotnet build "Iverson.Sql/Iverson.Sql.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet pack "Iverson.Sql/Iverson.Sql.csproj" -c Release -o /app/packages

FROM scratch AS export
COPY --from=publish /app/packages /packages
