# Iverson.Elasticsearch is a class library — it is packaged into Iverson.Api at build time.
# This Dockerfile builds just the library for standalone artifact inspection or packaging.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Iverson.Elasticsearch/Iverson.Elasticsearch.csproj", "Iverson.Elasticsearch/"]
RUN dotnet restore "Iverson.Elasticsearch/Iverson.Elasticsearch.csproj"

COPY Iverson.Elasticsearch/ Iverson.Elasticsearch/
RUN dotnet build "Iverson.Elasticsearch/Iverson.Elasticsearch.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet pack "Iverson.Elasticsearch/Iverson.Elasticsearch.csproj" -c Release -o /app/packages

FROM scratch AS export
COPY --from=publish /app/packages /packages
