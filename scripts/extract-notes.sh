#!/usr/bin/env bash
# Schneidet den Abschnitt einer Version aus CHANGELOG.md heraus und schreibt ihn
# als DnsPilot-<version>-notes.md. Diese Datei kommt ans GitHub-Release und
# auf den Update-Share — DNS-Pilot zeigt sie im Update-Dialog an.
#
# Aufruf: scripts/extract-notes.sh <version> [ausgabedatei]
# Bricht ab, wenn es zur Version keinen Abschnitt gibt: ein Release ohne
# Versionshinweise soll gar nicht erst entstehen. Muster aus QrCodeBuilder.
set -euo pipefail

VERSION="$1"
OUT="${2:-DnsPilot-${VERSION}-notes.md}"

# Abschnitt beginnt mit "## <version>" (danach Datum o. ä.) und endet vor dem
# nächsten "## ". Die Überschrift selbst gehört dazu, damit beim Überspringen
# mehrerer Versionen erkennbar bleibt, was zu welcher gehört.
awk -v v="$VERSION" '
  /^## / { inside = ($2 == v) }
  inside { print }
' CHANGELOG.md | sed -e :a -e '/^\n*$/{$d;N;ba' -e '}' > "$OUT"

if [ ! -s "$OUT" ]; then
  echo "::error::CHANGELOG.md enthält keinen Abschnitt '## ${VERSION}'." >&2
  rm -f "$OUT"
  exit 1
fi

echo "Versionshinweise geschrieben: $OUT"
cat "$OUT"
