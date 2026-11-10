# syntax=docker/dockerfile:1
# Image worker thu thập Radar tin (ADR-0008 §4) — CHƯA build thử trên máy dựng (không có Docker); CI chưa build image.
#   docker build -f backend/src/Tinhte.Worker/Dockerfile -t tinhte-worker .        (context = GỐC repo: cần config/ và backend/)
#   docker run --rm -e ConnectionStrings__Tinhte="Host=...;Username=...;Password=...;Database=..." -e Sites__Environment=staging tinhte-worker
# Migration KHÔNG chạy ở đây (Tinhte.DbMigrator là bước deploy riêng). Không mở cổng nào: worker chỉ gọi ra ngoài (feed RSS, webhook cảnh báo).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/Directory.Build.props backend/Directory.Packages.props backend/
COPY config/ config/
COPY backend/src/Tinhte.Platform/ backend/src/Tinhte.Platform/
COPY backend/src/Modules/Tinhte.Modules.Aggregation/ backend/src/Modules/Tinhte.Modules.Aggregation/
COPY backend/src/Tinhte.Worker/ backend/src/Tinhte.Worker/
RUN dotnet publish backend/src/Tinhte.Worker/Tinhte.Worker.csproj -c Release -o /app --no-self-contained -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV DOTNET_ENVIRONMENT=Production \
    Aggregation__Worker__LivenessFile=/tmp/tinhte-worker.alive
USER $APP_UID
# Sống = tệp nhịp tim được chạm trong 3 phút gần nhất (worker chỉ chạm khi vòng quét không lỗi hạ tầng: DB mất kết nối lâu → unhealthy).
HEALTHCHECK --interval=60s --timeout=5s --start-period=90s --retries=3 \
  CMD ["/bin/sh", "-c", "test -f /tmp/tinhte-worker.alive && test $(( $(date +%s) - $(stat -c %Y /tmp/tinhte-worker.alive) )) -lt 180"]
ENTRYPOINT ["dotnet", "Tinhte.Worker.dll"]
