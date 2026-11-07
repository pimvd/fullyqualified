FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/FullyQualified.Web -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "FullyQualified.Web.dll"]

# Separate images per component (ADR 0014): the role is part of the image, nothing to configure at deploy time.
#   docker build --target web    -t fq-web .
#   docker build --target worker -t fq-worker .
FROM runtime AS web
ENV Hosting__Role=web
EXPOSE 8080

FROM runtime AS worker
ENV Hosting__Role=worker

# Default target (a plain `docker build .`, used by docker-compose): the role comes from the environment, `all` when unset.
FROM runtime AS all
EXPOSE 8080
