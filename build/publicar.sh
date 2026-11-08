#!/usr/bin/env bash
# Genera los paquetes de una release de SheepGD en publish/:
#
#   SheepGD-win-x64.zip                    carpeta "SheepGD" con los dos lanzadores y las dos apps
#   SheepGDRequest-Linux-x86_64.AppImage
#   SheepGDEditor-Linux-x86_64.AppImage
#   SHA256SUMS.txt
#
# Los nombres no se pueden cambiar a la ligera: el aviso de actualización del programa
# (Services/ActualizacionService.cs) busca estos adjuntos en la release.
#
# La versión sale de Directory.Build.props. La release de GitHub se etiqueta con ella y una "v"
# delante (v1.1.0); si la etiqueta no coincide, el programa no reconoce la versión nueva.
#
# Uso:
#   build/publicar.sh                 todo
#   build/publicar.sh --solo-windows
#   build/publicar.sh --solo-linux
#   build/publicar.sh --subir         además crea la release en GitHub como BORRADOR (necesita la
#                                     herramienta gh con la sesión iniciada). Un borrador no le
#                                     llega a nadie hasta que se publica desde la web, así que da
#                                     tiempo a revisar las notas. Las notas se toman de
#                                     build/NOTAS-v<versión>.md si existe.
#
# Requisitos: SDK de .NET 9 o superior, zip y, para Linux, conexión a internet la primera vez
# (descarga appimagetool y su runtime en build/.herramientas). Se puede ejecutar desde cualquier
# carpeta.
set -euo pipefail

# Lo que cree este script nace legible por todos; los archivos copiados conservan sus permisos,
# y esos los arregla normalizar_permisos.
umask 022

RAIZ="$(cd "$(dirname "$(readlink -f "$0")")/.." && pwd)"
SALIDA="$RAIZ/publish"
HERRAMIENTAS="$RAIZ/build/.herramientas"
REPOSITORIO="PakeMPC/SheepGDRequest"

WINDOWS=1
LINUX=1
SUBIR=0
for opcion in "$@"; do
    case "$opcion" in
        --solo-windows) LINUX=0 ;;
        --solo-linux)   WINDOWS=0 ;;
        --subir)        SUBIR=1 ;;
        *) echo "Opción desconocida: $opcion" >&2; exit 2 ;;
    esac
done

fallo() { echo "ERROR: $*" >&2; exit 1; }
paso()  { echo; echo "==> $*"; }

# ------------------------------------------------------------------ comprobaciones
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$RAIZ/Directory.Build.props" | head -n1)"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fallo "No se pudo leer la versión de Directory.Build.props."

command -v dotnet >/dev/null || fallo "No se encuentra dotnet."
# Los generadores de Avalonia 12 necesitan Roslyn 4.14, que trae el SDK 9.
dotnet --list-sdks | awk -F. '$1 >= 9 { ok = 1 } END { exit !ok }' \
    || fallo "Hace falta el SDK de .NET 9 o superior (dotnet --list-sdks)."
command -v zip >/dev/null && command -v unzip >/dev/null || fallo "Faltan zip o unzip (sudo apt install zip unzip)."
command -v curl >/dev/null || fallo "Falta curl (sudo apt install curl)."
if [[ $SUBIR == 1 ]]; then
    command -v gh >/dev/null || fallo "--subir necesita la herramienta gh (https://cli.github.com)."
fi

echo "SheepGD v$VERSION -> $SALIDA"
mkdir -p "$SALIDA"

# Deja una carpeta con permisos estándar: carpetas y ejecutables 755, el resto 644. Hace falta
# porque los archivos llegan con los permisos de su origen (la caché de NuGet, un archivo copiado
# de otra máquina) y pueden quedar en 0600 o 0764. appimagetool rechaza un AppDir con archivos que
# otros usuarios no pueden leer: un AppImage lo ejecutan usuarios distintos del que lo empaquetó.
normalizar_permisos() {
    local carpeta="$1"
    find "$carpeta" -type d -exec chmod 755 {} +
    find "$carpeta" -type f -perm /111 -exec chmod 755 {} +
    find "$carpeta" -type f ! -perm /111 -exec chmod 644 {} +
}

# Publica una app autocontenida en una carpeta, sin símbolos de depuración (.pdb).
publicar_app() {
    local app="$1" rid="$2" destino="$3"
    rm -rf "$destino"
    dotnet publish "$RAIZ/$app/$app.csproj" -c Release -r "$rid" --self-contained true \
        -p:DebugType=none -p:DebugSymbols=false -o "$destino" --nologo -v quiet
    find "$destino" -name '*.pdb' -delete
    [[ -f "$destino/$app" || -f "$destino/$app.exe" ]] || fallo "La publicación de $app ($rid) no tiene ejecutable."
}

# ------------------------------------------------------------------ Windows
if [[ $WINDOWS == 1 ]]; then
    paso "Windows: publicando"
    CARPETA_WIN="$SALIDA/win/SheepGD"
    rm -rf "$SALIDA/win"
    mkdir -p "$CARPETA_WIN"
    publicar_app SheepGDRequest win-x64 "$CARPETA_WIN/SheepGDRequest"
    publicar_app SheepGDEditor  win-x64 "$CARPETA_WIN/SheepGDEditor"

    # Los lanzadores ya compilados (build/compilar-lanzadores.sh). Sin ellos la actualización
    # automática no se puede aplicar: el programa usa el lanzador de la versión nueva.
    for app in SheepGDRequest SheepGDEditor; do
        [[ -f "$RAIZ/build/win-launchers/$app.exe" ]] || fallo "Falta build/win-launchers/$app.exe."
        cp "$RAIZ/build/win-launchers/$app.exe" "$CARPETA_WIN/"
    done

    normalizar_permisos "$CARPETA_WIN"

    paso "Windows: comprimiendo"
    ZIP="$SALIDA/SheepGD-win-x64.zip"
    rm -f "$ZIP"
    (cd "$SALIDA/win" && zip -qr -X "$ZIP" SheepGD)

    contenido="$(unzip -Z1 "$ZIP")"
    for necesario in SheepGD/SheepGDRequest.exe SheepGD/SheepGDEditor.exe \
                     SheepGD/SheepGDRequest/SheepGDRequest.exe SheepGD/SheepGDEditor/SheepGDEditor.exe; do
        grep -qx "$necesario" <<<"$contenido" || fallo "El zip no contiene $necesario."
    done
    ! grep -q '\.pdb$' <<<"$contenido" || fallo "El zip contiene archivos .pdb."
fi

# ------------------------------------------------------------------ Linux
if [[ $LINUX == 1 ]]; then
    APPIMAGETOOL="$HERRAMIENTAS/appimagetool-x86_64.AppImage"
    if [[ ! -x "$APPIMAGETOOL" ]]; then
        paso "Descargando appimagetool"
        mkdir -p "$HERRAMIENTAS"
        curl -fL --retry 3 -o "$APPIMAGETOOL.descarga" \
            https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
        chmod 755 "$APPIMAGETOOL.descarga"
        mv "$APPIMAGETOOL.descarga" "$APPIMAGETOOL"
    fi

    # El runtime es la parte del AppImage que lo monta y lo arranca. appimagetool lo descarga en
    # cada ejecución y, si GitHub falla en ese momento (un 502), no crea nada; descargado aquí una
    # vez, con reintentos, sirve para las dos apps y para las siguientes releases.
    RUNTIME="$HERRAMIENTAS/runtime-x86_64"
    if [[ ! -s "$RUNTIME" ]]; then
        paso "Descargando el runtime de AppImage"
        mkdir -p "$HERRAMIENTAS"
        curl -fL --retry 5 --retry-all-errors --retry-delay 3 -o "$RUNTIME.descarga" \
            https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-x86_64
        mv "$RUNTIME.descarga" "$RUNTIME"
    fi

    crear_appimage() {
        local app="$1"
        local id; id="$(tr '[:upper:]' '[:lower:]' <<<"$app")"
        local appdir="$SALIDA/linux/$app.AppDir"

        paso "Linux: $app"
        rm -rf "$appdir"
        mkdir -p "$appdir/usr/bin"
        publicar_app "$app" linux-x64 "$appdir/usr/bin"

        # El punto de entrada del AppImage: arranca el ejecutable publicado.
        cat > "$appdir/AppRun" <<EOF
#!/bin/sh
AQUI="\$(dirname "\$(readlink -f "\$0")")"
exec "\$AQUI/usr/bin/$app" "\$@"
EOF
        chmod 755 "$appdir/AppRun"

        cat > "$appdir/$id.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=$app
Exec=$app
Icon=$id
Categories=Utility;
Terminal=false
X-AppImage-Version=$VERSION
EOF
        cp "$RAIZ/build/$app.png" "$appdir/$id.png"
        ln -s "$id.png" "$appdir/.DirIcon"
        normalizar_permisos "$appdir"

        local salida="$SALIDA/$app-Linux-x86_64.AppImage"
        rm -f "$salida"
        # APPIMAGE_EXTRACT_AND_RUN: appimagetool también es un AppImage, y así no necesita FUSE.
        ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$APPIMAGETOOL" -n --runtime-file "$RUNTIME" "$appdir" "$salida" >/dev/null 2>"$SALIDA/linux/$app.appimagetool.log" \
            || { cat "$SALIDA/linux/$app.appimagetool.log" >&2; fallo "appimagetool falló con $app."; }
        chmod 755 "$salida"

        # Marca de AppImage de tipo 2 ("AI" y 0x02 en el byte 8): la misma que comprueba el programa
        # antes de reemplazarse.
        [[ "$(head -c 11 "$salida" | tail -c 3 | od -An -tx1 | tr -d ' \n')" == "414902" ]] \
            || fallo "$salida no tiene la marca de AppImage."
    }

    crear_appimage SheepGDRequest
    crear_appimage SheepGDEditor
fi

# ------------------------------------------------------------------ resumen
paso "Resultado"
cd "$SALIDA"
archivos=()
[[ $WINDOWS == 1 ]] && archivos+=(SheepGD-win-x64.zip)
[[ $LINUX == 1 ]] && archivos+=(SheepGDRequest-Linux-x86_64.AppImage SheepGDEditor-Linux-x86_64.AppImage)
sha256sum "${archivos[@]}" > SHA256SUMS.txt
ls -lh "${archivos[@]}" SHA256SUMS.txt | awk '{ print $1, $5, $9 }'

if [[ $SUBIR == 1 ]]; then
    paso "GitHub: creando la release v$VERSION como borrador"
    NOTAS="$RAIZ/build/NOTAS-v$VERSION.md"
    if [[ -f "$NOTAS" ]]; then notas=(--notes-file "$NOTAS"); else notas=(--generate-notes); fi
    gh release create "v$VERSION" "${archivos[@]}" SHA256SUMS.txt \
        --repo "$REPOSITORIO" --title "SheepGD v$VERSION" --draft "${notas[@]}"
    echo "Revisa el borrador en https://github.com/$REPOSITORIO/releases y publícalo."
else
    echo
    echo "Siguiente paso: crea en GitHub la release con la etiqueta v$VERSION (no como pre-release)"
    echo "y sube estos archivos tal cual, sin renombrarlos. O vuelve a ejecutar con --subir."
fi
