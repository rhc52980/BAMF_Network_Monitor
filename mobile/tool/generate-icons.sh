#!/usr/bin/env bash
# Draws the app's launcher icons and splash screens from BAMF's own star
# (BAMF/wwwroot/bamf-icon.svg), replacing Capacitor's placeholders. Run from
# anywhere; needs rsvg-convert and ImageMagick (brew install librsvg imagemagick).
#
#   bash mobile/tool/generate-icons.sh
#
# Android gets an adaptive icon (dark background colour + the star on a
# transparent layer), the older square and round icons for API 24-25, a
# monochrome layer for themed icons on Android 13+, and dark splash images.
# iPhone gets the 1024px icon (opaque, iOS rounds the corners) and the splash.
set -euo pipefail

BG="#10141a"                               # the dashboard's background
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SVG="$ROOT/BAMF/wwwroot/bamf-icon.svg"
RES="$ROOT/mobile/android/app/src/main/res"
IOS="$ROOT/mobile/ios/App/App/Assets.xcassets"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
command -v rsvg-convert >/dev/null && command -v magick >/dev/null || { echo "Needs rsvg-convert and magick."; exit 1; }

# A star of the given pixel width, transparent around it, rendered sharp at that size.
star() { rsvg-convert -w "$1" -h "$1" "$SVG" -o "$2"; }

# The star as a one-colour silhouette with its lines and nodes cut out, for themed icons.
python3 - "$SVG" "$TMP" <<'PY'
import re, sys
src, tmp = open(sys.argv[1]).read(), sys.argv[2]
head = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 120">'
poly = re.search(r'<polygon.*?/>', src, re.S).group(0)
rest = "".join(re.findall(r'<(?:line|circle)[^>]*/>', src))
def black(s): return re.sub(r'(fill|stroke)="#[0-9a-fA-F]+"', r'\1="#000"', s)
open(f"{tmp}/body.svg", "w").write(head + black(poly) + "</svg>")
open(f"{tmp}/cut.svg", "w").write(head + black(rest) + "</svg>")
PY

# --- Android launcher icons -------------------------------------------------
sizes_fg=(mdpi:108 hdpi:162 xhdpi:216 xxhdpi:324 xxxhdpi:432)
sizes_legacy=(mdpi:48 hdpi:72 xhdpi:96 xxhdpi:144 xxxhdpi:192)
for e in "${sizes_fg[@]}"; do
  d="${e%%:*}"; S="${e##*:}"; W=$(( S * 60 / 100 ))     # star inside the adaptive icon's safe circle
  star "$W" "$TMP/s.png"
  magick -size ${S}x${S} xc:none "$TMP/s.png" -gravity center -composite "$RES/mipmap-$d/ic_launcher_foreground.png"
  rsvg-convert -w "$W" -h "$W" "$TMP/body.svg" -o "$TMP/b.png"; rsvg-convert -w "$W" -h "$W" "$TMP/cut.svg" -o "$TMP/c.png"
  magick "$TMP/b.png" "$TMP/c.png" -compose DstOut -composite "$TMP/m.png"
  magick -size ${S}x${S} xc:none "$TMP/m.png" -gravity center -composite "$RES/mipmap-$d/ic_launcher_monochrome.png"
done
for e in "${sizes_legacy[@]}"; do
  d="${e%%:*}"; S="${e##*:}"; R=$(( S * 18 / 100 ))
  star $(( S * 76 / 100 )) "$TMP/s.png"
  magick -size ${S}x${S} xc:none -fill "$BG" -draw "roundrectangle 0,0 $((S-1)),$((S-1)) $R,$R" "$TMP/s.png" -gravity center -composite "$RES/mipmap-$d/ic_launcher.png"
  star $(( S * 66 / 100 )) "$TMP/s.png"
  magick -size ${S}x${S} xc:none -fill "$BG" -draw "circle $((S/2)),$((S/2)) $((S/2)),0" "$TMP/s.png" -gravity center -composite "$RES/mipmap-$d/ic_launcher_round.png"
done
cat > "$RES/values/ic_launcher_background.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<resources>
    <color name="ic_launcher_background">$(echo "$BG" | tr a-f A-F)</color>
</resources>
XML
for f in ic_launcher ic_launcher_round; do
cat > "$RES/mipmap-anydpi-v26/$f.xml" <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
    <background android:drawable="@color/ic_launcher_background"/>
    <foreground android:drawable="@mipmap/ic_launcher_foreground"/>
    <monochrome android:drawable="@mipmap/ic_launcher_monochrome"/>
</adaptive-icon>
XML
done

# --- Android splash: dark, with the star centred (same sizes as the placeholders) ---
for f in "$RES"/drawable*/splash.png; do
  dim="$(identify -format '%wx%h' "$f")"; w="${dim%x*}"; h="${dim#*x}"; m=$(( w < h ? w : h ))
  star $(( m * 30 / 100 )) "$TMP/s.png"
  magick -size "$dim" "xc:$BG" "$TMP/s.png" -gravity center -composite "$f"
done

# --- iPhone ---------------------------------------------------------------------
star 700 "$TMP/s.png"
magick -size 1024x1024 "xc:$BG" "$TMP/s.png" -gravity center -composite -alpha off "$IOS/AppIcon.appiconset/AppIcon-512@2x.png"
star 760 "$TMP/s.png"
for f in "$IOS"/Splash.imageset/splash-2732x2732*.png; do
  magick -size 2732x2732 "xc:$BG" "$TMP/s.png" -gravity center -composite -alpha off "$f"
done
echo "Icons and splash screens drawn from $(basename "$SVG")."
