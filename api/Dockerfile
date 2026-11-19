FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY BackupControl.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /out --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
RUN mkdir /app/keys && chown -R app:app /app
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "BackupControl.dll"]
