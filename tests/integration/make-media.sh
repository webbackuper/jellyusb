#!/usr/bin/env bash
# Builds a small test library imitating the real setup:
#   <dir>/MOVIES98/MOVIES98/*.mkv   (drive that stays "plugged in")
#   <dir>/MOVIES97/MOVIES97/*.mkv   (drive that will be "unplugged" during the tests)
# NFO files are written in Windows-1252 like the ones on MOVIES01, and one MKV has
# double-encoded chapter names like the real "La Cage dorée".
set -eu
DIR=${1:?usage: make-media.sh <dir>}
FFMPEG=${FFMPEG:-/usr/lib/jellyfin-ffmpeg/ffmpeg}
mkdir -p "$DIR/MOVIES98/MOVIES98" "$DIR/MOVIES97/MOVIES97"

video() { # <output> [ffmetadata]
    local meta=()
    if [ -n "${2:-}" ]; then meta=(-i "$2" -map_metadata 2 -map_chapters 2); fi
    "$FFMPEG" -hide_banner -loglevel error -y \
        -f lavfi -i "testsrc2=size=640x360:rate=25:duration=20" \
        -f lavfi -i "sine=frequency=440:duration=20" \
        "${meta[@]}" -map 0:v -map 1:a -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -shortest "$1"
}

# NFO in Windows-1252 (python writes the bytes; the XML declaration says ISO-8859-1 like old tools did)
nfo1252() { # <output> <title> <originaltitle> <plot> <year>
    python3 -I - "$@" <<'EOF'
import sys
out, title, orig, plot, year = sys.argv[1:6]
xml = f'''<?xml version="1.0" encoding="ISO-8859-1" standalone="yes"?>
<movie>
  <title>{title}</title>
  <originaltitle>{orig}</originaltitle>
  <plot>{plot}</plot>
  <year>{year}</year>
  <genre>Comédie</genre>
</movie>
'''
open(out, "wb").write(xml.encode("cp1252"))
EOF
}

M98="$DIR/MOVIES98/MOVIES98"
video "$M98/Larguées [Snatched](de Jonathan Levine - 2017).mkv"
nfo1252 "$M98/Larguées [Snatched](de Jonathan Levine - 2017).nfo" "Larguées" "Snatched" "Une mère et sa fille partent en vacances… Ça tourne mal." 2017

video "$M98/Le Prix de la Verite [Truth][1080p-Multi](de James Vanderbilt - 2015).mkv"
nfo1252 "$M98/Le Prix de la Verite [Truth][1080p-Multi](de James Vanderbilt - 2015).nfo" "Truth : Le Prix de la vérité" "Truth" "Révélations d'une journaliste." 2015

cat > "$DIR/chapters.txt" <<'EOF'
;FFMETADATA1
title=La Cage dorée

[CHAPTER]
TIMEBASE=1/1000
START=0
END=10000
title= L'HÃ©ritage

[CHAPTER]
TIMEBASE=1/1000
START=10000
END=20000
title=CapÃ­tulo 02
EOF
video "$M98/La Cage doree (2013).mkv" "$DIR/chapters.txt"
printf '<?xml version="1.0" encoding="utf-8"?>\n<movie><title>La Cage dorée</title><year>2013</year></movie>\n' > "$M98/La Cage doree (2013).nfo"

video "$M98/Fichier Supprime (2020).mkv"

M97="$DIR/MOVIES97/MOVIES97"
video "$M97/Le Fabuleux Destin d'Amelie Poulain (2001).mkv"
nfo1252 "$M97/Le Fabuleux Destin d'Amelie Poulain (2001).nfo" "Le Fabuleux Destin d'Amélie Poulain" "Le Fabuleux Destin d'Amélie Poulain" "Amélie, une serveuse de Montmartre." 2001

rm -f "$DIR/chapters.txt"
echo "Test media written to $DIR"
find "$DIR" -type f | sort
