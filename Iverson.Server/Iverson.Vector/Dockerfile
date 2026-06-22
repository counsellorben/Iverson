# Iverson.Vector is a class library — it is packaged into Iverson.Api at build time.
# This Dockerfile builds just the library for standalone artifact inspection or packaging.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Iverson.Vector/Iverson.Vector.csproj", "Iverson.Vector/"]
RUN dotnet restore "Iverson.Vector/Iverson.Vector.csproj"

COPY Iverson.Vector/ Iverson.Vector/
RUN dotnet build "Iverson.Vector/Iverson.Vector.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet pack "Iverson.Vector/Iverson.Vector.csproj" -c Release -o /app/packages

FROM scratch AS export
COPY --from=publish /app/packages /packages
