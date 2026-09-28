# Контекст сборки — корень репозитория (нужна папка proto/).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY proto/ proto/
COPY backend/global.json backend/Icc.slnx backend/Directory.Build.props backend/
COPY backend/src/Icc.ControlPlane/Icc.ControlPlane.csproj backend/src/Icc.ControlPlane/
RUN dotnet restore backend/src/Icc.ControlPlane/Icc.ControlPlane.csproj
COPY backend/src/ backend/src/
RUN dotnet publish backend/src/Icc.ControlPlane/Icc.ControlPlane.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Каталог ключей CA центра (монтируется томом, владелец — пользователь приложения).
RUN mkdir -p /data/pki && chown $APP_UID /data/pki
USER $APP_UID
EXPOSE 8080 9090
ENTRYPOINT ["dotnet", "Icc.ControlPlane.dll"]
