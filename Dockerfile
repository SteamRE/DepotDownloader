FROM mcr.microsoft.com/dotnet/sdk:6.0-alpine AS build

ENV DOTNET_CLI_TELEMETRY_OPTOUT=true

WORKDIR /DepotDownloader

COPY ./ ./

RUN dotnet restore

RUN dotnet publish -c Release -o out

FROM mcr.microsoft.com/dotnet/runtime:6.0-alpine

ENV DOTNET_CLI_TELEMETRY_OPTOUT=true

WORKDIR /DepotDownloader

COPY --from=build /DepotDownloader/out .

ENTRYPOINT ["dotnet", "DepotDownloader.dll"]
