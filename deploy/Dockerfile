FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG SERVICE
WORKDIR /source
COPY Directory.Build.props global.json ./
COPY src/ ./src/
RUN case "$SERVICE" in \
      identity) service=Identity ;; \
      life) service=Life ;; \
      learning) service=Learning ;; \
      *) echo "Unsupported service: $SERVICE" >&2; exit 1 ;; \
    esac \
    && dotnet restore "src/PersonalFlow.${service}.Api/PersonalFlow.${service}.Api.csproj" \
    && dotnet publish "src/PersonalFlow.${service}.Api/PersonalFlow.${service}.Api.csproj" -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS runtime
ARG SERVICE
ENV ASPNETCORE_HTTP_PORTS=8080 ASPNETCORE_ENVIRONMENT=Production
WORKDIR /app
RUN case "$SERVICE" in \
      identity) service=Identity ;; \
      life) service=Life ;; \
      learning) service=Learning ;; \
      *) echo "Unsupported service: $SERVICE" >&2; exit 1 ;; \
    esac \
    && printf '%s' "$service" > /app/service-name \
    && apt-get update && apt-get install -y --no-install-recommends curl gosu ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /var/lib/personalflow/data-protection /run/personalflow-secrets \
    && chown -R ${APP_UID}:${APP_UID} /var/lib/personalflow \
    && chmod 0750 /var/lib/personalflow \
    && chmod 0755 /run/personalflow-secrets
COPY --from=build /out/ ./
COPY deploy/api-entrypoint.sh /usr/local/bin/personalflow-entrypoint
RUN chmod 0755 /usr/local/bin/personalflow-entrypoint
USER root
ENTRYPOINT ["/usr/local/bin/personalflow-entrypoint"]
