#!/bin/sh
set -eu

app_uid="${APP_UID:-1654}"
copy_secret_path() {
    variable="$1"
    eval "source_path=\${$variable:-}"
    case "$source_path" in
        /run/secrets/*)
            destination="/run/personalflow-secrets/$(basename "$source_path")"
            install -m 0400 "$source_path" "$destination"
            chown "$app_uid:$app_uid" "$destination"
            export "$variable=$destination"
            ;;
    esac
}

copy_secret_path ConnectionStrings__DatabaseFile
copy_secret_path Signing__Certificate
copy_secret_path Signing__PasswordFile
copy_secret_path Publishing__ApiKeyFile
copy_secret_path Owner__PasswordFile
service="$(cat /app/service-name)"
exec gosu "$app_uid:$app_uid" dotnet "/app/PersonalFlow.${service}.Api.dll" "$@"
