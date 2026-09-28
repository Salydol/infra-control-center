# Один Dockerfile для всех сервисов магазина: docker build --build-arg SERVICE=Shop.Api .
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY global.json Shop.slnx Directory.Build.props ./
COPY src/ src/
RUN dotnet restore Shop.slnx
ARG SERVICE
# BUILD_FAULT — дефект для сбоев «неудачный деплой» (только стенд), по умолчанию none.
ARG BUILD_FAULT=none
RUN dotnet publish src/${SERVICE}/${SERVICE}.csproj -c Release -o /app --no-restore -p:BuildFault=${BUILD_FAULT} \
 && ln -s /app/${SERVICE}.dll /app/service.dll

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
# Npgsql загружает GSSAPI при подключении; без библиотеки в логах шум.
RUN apk add --no-cache krb5-libs
WORKDIR /app
COPY --from=build /app .
ENV DOTNET_USE_POLLING_FILE_WATCHER=true
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=3 \
  CMD wget -qO- http://127.0.0.1:8080/healthz || exit 1
# Уникальный идентификатор сборки: каждая версия — отдельный образ.
ARG BUILD_ID=dev
LABEL shop.build-id=${BUILD_ID}
ENTRYPOINT ["dotnet", "service.dll"]
