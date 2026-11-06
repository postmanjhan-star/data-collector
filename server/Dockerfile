# Reference receiver; add an HTTPS reverse proxy and operational backups for deployment.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder
WORKDIR /source
COPY server/HF811Receiver.csproj ./server/
RUN dotnet restore ./server/HF811Receiver.csproj
COPY server/ ./server/
RUN dotnet publish ./server/HF811Receiver.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8090
ENV HF811_RECEIVER_DB=/app/data/receiver.sqlite3
RUN mkdir -p /app/data && chown -R $APP_UID:$APP_UID /app/data
COPY --from=builder --chown=$APP_UID:$APP_UID /out/ ./
USER $APP_UID
EXPOSE 8090
ENTRYPOINT ["dotnet", "HF811Receiver.dll"]
