#!/usr/bin/env bash
# Compila los dos lanzadores de Windows (build/win-launchers/*.exe) desde lanzador.c, con el icono
# de cada app. Solo hace falta si cambia lanzador.c o alguno de los .ico: los .exe están
# versionados y publicar.sh se limita a copiarlos.
#
# Requiere MinGW: sudo apt install gcc-mingw-w64-x86-64
#   -municode -> entra por wmain · -mwindows -> subsistema WINDOWS, sin consola
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"

mkdir -p win-launchers
for app in SheepGDRequest SheepGDEditor; do
    # Hay un .ico por app, así que el .rc se genera al paso y se borra.
    printf '1 ICON "%s.ico"\n' "$app" > "$app.rc"
    x86_64-w64-mingw32-windres "$app.rc" -O coff -o "$app.res.o"
    x86_64-w64-mingw32-gcc -municode -mwindows -s -Os -Wall \
        -DAPP=L\"$app\" lanzador.c "$app.res.o" -o "win-launchers/$app.exe"
    rm "$app.rc" "$app.res.o"
    echo "win-launchers/$app.exe"
done
