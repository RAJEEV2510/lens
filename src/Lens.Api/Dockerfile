FROM node:22-alpine AS web
WORKDIR /web
COPY src/Lens.Web/package*.json ./
RUN npm ci
COPY src/Lens.Web .
RUN npx ng build --configuration production --output-path /web/dist

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Lens.sln .
COPY db/init.sql db/
COPY src/Lens.Core/Lens.Core.csproj src/Lens.Core/
COPY src/Lens.Api/Lens.Api.csproj src/Lens.Api/
RUN dotnet restore src/Lens.Api/Lens.Api.csproj
COPY src/Lens.Core src/Lens.Core
COPY src/Lens.Api src/Lens.Api
COPY --from=web /web/dist/browser src/Lens.Api/wwwroot
RUN dotnet publish src/Lens.Api/Lens.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Lens.Api.dll"]
