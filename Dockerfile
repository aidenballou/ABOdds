FROM mcr.microsoft.com/dotnet/sdk:10.0.103 AS build
WORKDIR /source
COPY ABOdds.slnx Directory.Build.props ./
COPY src/ABOdds/ABOdds.csproj src/ABOdds/
RUN dotnet restore src/ABOdds/ABOdds.csproj
COPY . .
RUN dotnet publish src/ABOdds/ABOdds.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0.3 AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "ABOdds.dll"]
