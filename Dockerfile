# syntax=docker/dockerfile:1
# Work CI supplies approved JFrog/mirrored images including @sha256 digests.
ARG SDK_IMAGE
ARG RUNTIME_IMAGE
FROM ${SDK_IMAGE} AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/ ./src/
RUN --mount=type=secret,id=nuget_config,target=/tmp/NuGet.Config,required=true \
    dotnet restore src/Sender.Worker/Sender.Worker.csproj --locked-mode \
    --configfile /tmp/NuGet.Config
RUN dotnet publish src/Sender.Worker/Sender.Worker.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM ${RUNTIME_IMAGE} AS runtime
WORKDIR /app
COPY --from=build /out/ ./
ENV ASPNETCORE_HTTP_PORTS=8080 ASPNETCORE_URLS=http://+:8080 DOTNET_ENVIRONMENT=Production
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Sender.Worker.dll"]
