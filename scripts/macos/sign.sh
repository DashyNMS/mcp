#!/usr/bin/env bash
# Sign one executable with a Developer ID and have Apple notarise it, as DashyNMS
# desktop signs its app. A bare executable can't carry a notarisation ticket, so
# macOS fetches the one this makes the first time the executable is opened.
#
# Usage: sign.sh <executable>
# From the environment: MACOS_CERTIFICATE_BASE64, MACOS_CERTIFICATE_PASSWORD,
# MACOS_SIGNING_IDENTITY, APPLE_API_KEY, APPLE_API_KEY_ID, APPLE_API_ISSUER_ID.
set -euo pipefail
executable=$1
entitlements=$(dirname "$0")/jit.entitlements.plist
for name in MACOS_CERTIFICATE_BASE64 MACOS_CERTIFICATE_PASSWORD MACOS_SIGNING_IDENTITY APPLE_API_KEY APPLE_API_KEY_ID APPLE_API_ISSUER_ID; do
  if [ -z "${!name:-}" ]; then echo "::error::$name is not set."; exit 1; fi
done

work=$(mktemp -d)
keychain=$work/signing.keychain-db
keychain_password=$(openssl rand -hex 32)
# Nothing here prints a secret: no -x, and the credentials are only ever files in $work.
cleanup() {
  security delete-keychain "$keychain" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

umask 077
printf '%s' "$MACOS_CERTIFICATE_BASE64" | base64 --decode > "$work/certificate.p12"
printf '%s' "$APPLE_API_KEY" > "$work/key.p8"
security create-keychain -p "$keychain_password" "$keychain"
security set-keychain-settings -lut 21600 "$keychain"
security unlock-keychain -p "$keychain_password" "$keychain"
security import "$work/certificate.p12" -k "$keychain" -P "$MACOS_CERTIFICATE_PASSWORD" -T /usr/bin/codesign >/dev/null
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$keychain_password" "$keychain" >/dev/null
existing=()
while IFS= read -r line; do existing+=("$(sed -e 's/^ *"//' -e 's/"$//' <<<"$line")"); done < <(security list-keychains -d user)
security list-keychains -d user -s "${existing[@]}" "$keychain"

codesign --force --sign "$MACOS_SIGNING_IDENTITY" --keychain "$keychain" --options runtime --timestamp --entitlements "$entitlements" "$executable"
codesign --verify --strict "$executable"

ditto -c -k "$executable" "$work/submission.zip"
xcrun notarytool submit "$work/submission.zip" --key "$work/key.p8" --key-id "$APPLE_API_KEY_ID" --issuer "$APPLE_API_ISSUER_ID" \
  --wait --timeout 30m --output-format json > "$work/notary.json"
if [ "$(/usr/bin/plutil -extract status raw -o - "$work/notary.json")" != Accepted ]; then
  echo "::error::Apple did not accept the notarisation. Its log follows."
  xcrun notarytool log "$(/usr/bin/plutil -extract id raw -o - "$work/notary.json")" --key "$work/key.p8" --key-id "$APPLE_API_KEY_ID" --issuer "$APPLE_API_ISSUER_ID" || true
  exit 1
fi
echo "Signed and notarised."
