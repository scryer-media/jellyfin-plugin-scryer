#!/usr/bin/env bash

set -euo pipefail

manifest_url="${1:-https://raw.githubusercontent.com/scryer-media/jellyfin-plugin-scryer/main/manifest.json}"
jellyfin_image="${JELLYFIN_IMAGE:-jellyfin/jellyfin:10.11.11}"
keep_container="${SCRYER_SMOKE_KEEP_CONTAINER:-0}"
test_root="$(mktemp -d /tmp/scryer-catalog-smoke.XXXXXX)"
container_name="scryer-catalog-smoke-$$"
admin_name="catalogadmin"
admin_password="CatalogSmokeOnly!2026"

mkdir -p "${test_root}/config" "${test_root}/cache"

cleanup() {
  docker stop "${container_name}" >/dev/null 2>&1 || true
  if [[ "${keep_container}" == "1" ]]; then
    printf 'Retained stopped container %s and fixture %s\n' "${container_name}" "${test_root}"
    return
  fi

  docker rm "${container_name}" >/dev/null 2>&1 || true
  if [[ "${test_root}" == /tmp/scryer-catalog-smoke.* ]]; then
    rm -rf "${test_root}"
  fi
}
trap cleanup EXIT

pick_port() {
  python3 -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()'
}

wait_for_status() {
  local url="$1"
  local expected="$2"
  local attempts="${3:-90}"
  local code=""

  for ((i = 0; i < attempts; i++)); do
    code="$(curl -sS -o /dev/null -w '%{http_code}' "${url}" 2>/dev/null || true)"
    if [[ "${code}" == "${expected}" ]]; then
      return 0
    fi
    sleep 1
  done

  printf 'Timed out waiting for %s to return %s (last status: %s)\n' "${url}" "${expected}" "${code}" >&2
  return 1
}

authenticate() {
  local base_url="$1"
  local response=""
  local token=""

  for ((i = 0; i < 90; i++)); do
    response="$(
      curl -sS -X POST "${base_url}/Users/AuthenticateByName" \
        -H 'Content-Type: application/json' \
        -H 'X-Emby-Authorization: MediaBrowser Client="ScryerCatalogSmoke", Device="Local", DeviceId="scryer-catalog-smoke", Version="1.0"' \
        --data-binary "{\"Username\":\"${admin_name}\",\"Pw\":\"${admin_password}\"}" \
        2>/dev/null || true
    )"
    token="$(printf '%s' "${response}" | jq -r '.AccessToken // empty' 2>/dev/null || true)"
    if [[ -n "${token}" ]]; then
      printf '%s' "${token}"
      return 0
    fi
    sleep 1
  done

  printf 'Timed out authenticating the Jellyfin smoke-test administrator\n' >&2
  return 1
}

port="$(pick_port)"
base_url="http://127.0.0.1:${port}"

docker run -d \
  --name "${container_name}" \
  -p "127.0.0.1:${port}:8096" \
  -v "${test_root}/config:/config" \
  -v "${test_root}/cache:/cache" \
  "${jellyfin_image}" >/dev/null

wait_for_status "${base_url}/Startup/Configuration" 200

curl -fsS -X POST "${base_url}/Startup/Configuration" \
  -H 'Content-Type: application/json' \
  --data-binary '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
curl -fsS "${base_url}/Startup/User" >/dev/null
curl -fsS -X POST "${base_url}/Startup/User" \
  -H 'Content-Type: application/json' \
  --data-binary "{\"Name\":\"${admin_name}\",\"Password\":\"${admin_password}\"}" >/dev/null
curl -fsS -X POST "${base_url}/Startup/RemoteAccess" \
  -H 'Content-Type: application/json' \
  --data-binary '{"EnableRemoteAccess":false,"EnableAutomaticPortMapping":false}' >/dev/null
curl -fsS -X POST "${base_url}/Startup/Complete" >/dev/null

token="$(authenticate "${base_url}")"
repositories="$(curl -fsS "${base_url}/Repositories" -H "X-Emby-Token: ${token}")"
repositories="$(
  printf '%s' "${repositories}" | jq \
    --arg url "${manifest_url}" \
    '. + [{Name:"Scryer", Url:$url, Enabled:true}] | unique_by(.Url)'
)"
curl -fsS -X POST "${base_url}/Repositories" \
  -H "X-Emby-Token: ${token}" \
  -H 'Content-Type: application/json' \
  --data-binary "${repositories}" >/dev/null

catalog="$(curl -fsS "${base_url}/Packages" -H "X-Emby-Token: ${token}")"
package="$(printf '%s' "${catalog}" | jq -ce '.[] | select((.name // "") == "Scryer")')"
guid="$(printf '%s' "${package}" | jq -r '.guid')"
version="$(printf '%s' "${package}" | jq -r '.versions[0].version')"
target_abi="$(printf '%s' "${package}" | jq -r '.versions[0].targetAbi')"

printf 'Catalog found Scryer %s for Jellyfin ABI %s\n' "${version}" "${target_abi}"

curl -fsS --get -X POST "${base_url}/Packages/Installed/Scryer" \
  -H "X-Emby-Token: ${token}" \
  --data-urlencode "assemblyGuid=${guid}" \
  --data-urlencode "version=${version}" \
  --data-urlencode "repositoryUrl=${manifest_url}" >/dev/null

plugin_dir="${test_root}/config/plugins/Scryer_${version}"
for ((i = 0; i < 90; i++)); do
  if [[ -f "${plugin_dir}/Jellyfin.Plugin.Scryer.dll" && -f "${plugin_dir}/meta.json" ]]; then
    break
  fi
  sleep 1
done
test -f "${plugin_dir}/Jellyfin.Plugin.Scryer.dll"
test -f "${plugin_dir}/meta.json"

docker restart "${container_name}" >/dev/null
wait_for_status "${base_url}/System/Info/Public" 200
sleep 2
wait_for_status "${base_url}/System/Info/Public" 200

token="$(authenticate "${base_url}")"
plugins="$(curl -fsS "${base_url}/Plugins" -H "X-Emby-Token: ${token}")"
pages="$(curl -fsS "${base_url}/Web/ConfigurationPages" -H "X-Emby-Token: ${token}")"
configuration_page="$(curl -fsS "${base_url}/web/ConfigurationPage?name=Scryer" -H "X-Emby-Token: ${token}")"

printf '%s' "${plugins}" | jq -e \
  --arg version "${version}" \
  '.[] | select(.Name == "Scryer" and .Version == $version and .Status == "Active")' >/dev/null
printf '%s' "${pages}" | jq -e \
  '.[] | select(.Name == "Scryer" and .DisplayName == "Scryer")' >/dev/null
[[ "${configuration_page}" == *'id="OAuthClientId"'* ]]
[[ "${configuration_page}" == *'id="ScryerInternalBaseUrl"'* ]]
[[ "${configuration_page}" != *'id="ScryerApiKey"'* ]]
logs="$(docker logs "${container_name}" 2>&1)"
[[ "${logs}" == *"Loaded plugin: Scryer ${version}"* ]]

printf 'PASS: Jellyfin catalog install, restart, activation, and current OAuth configuration page\n'
