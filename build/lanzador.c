// lanzador.c — Arranca <carpeta de este exe>\APP\APP.exe
//
// El nombre de la app llega por -D al compilar, así que el MISMO archivo
// genera los dos lanzadores. Todo en wchar: los nombres de usuario con
// acentos y las rutas con espacios son la norma en Windows, no la excepción.
//
// MODO ACTUALIZACIÓN. SheepGDRequest no puede reemplazar sus propios archivos
// mientras está abierto (Windows los tiene bloqueados). Lo que hace es
// descargar y descomprimir la versión nueva en <raíz>\.actualizacion, arrancar
// el lanzador de ESA versión nueva así y cerrarse:
//
//     <paquete>\SheepGDRequest.exe --aplicar-actualizacion <pid> "<raíz>"
//
// <paquete> es la carpeta donde está este exe (la que trae las carpetas
// SheepGDRequest y SheepGDEditor nuevas) y <raíz> la instalación que hay que
// actualizar. Entonces este lanzador:
//   1. Espera a que termine el proceso <pid> (hasta 60 s).
//   2. Aparta las carpetas viejas: <raíz>\APP pasa a <raíz>\APP.anterior.<n>.
//      Si alguna está en uso (SheepGDEditor abierto, por ejemplo), lo reintenta
//      unos segundos y luego pide al usuario que lo cierre.
//   3. Mueve las carpetas nuevas a la raíz. Si algo falla, deshace los pasos
//      2 y 3 y la instalación queda como estaba.
//   4. Reemplaza los lanzadores de la raíz por los nuevos (si falla, siguen los
//      viejos, que también sirven).
//   5. Abre la versión nueva.
// Las carpetas y lanzadores apartados, y la carpeta .actualizacion (desde la
// que se ejecuta este exe, que no puede borrarse a sí mismo), los borra
// SheepGDRequest en su siguiente arranque (ActualizacionService.LimpiarRestos).
//
// Usar el lanzador de la versión nueva, y no el de la raíz, permite que la
// actualización funcione aunque el de la raíz sea de una versión sin este modo.

#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>

#ifndef APP
#define APP L"SheepGDRequest"
#endif

#define LARGO (MAX_PATH * 2)
#define NUM_APPS 2

static const wchar_t *const APPS[NUM_APPS] = { L"SheepGDRequest", L"SheepGDEditor" };

static const wchar_t *const AVISO_EN_USO =
    L"No se pudo terminar la actualización: hay archivos de SheepGD en uso.\n"
    L"Cierra SheepGDEditor (y cualquier otra ventana de SheepGD) y pulsa Reintentar.\n\n"
    L"The update could not be finished: some SheepGD files are in use.\n"
    L"Close SheepGDEditor (and any other SheepGD window) and press Retry.";

static const wchar_t *const AVISO_FALLO =
    L"No se pudo aplicar la actualización. Se abrirá la versión que ya tenías.\n\n"
    L"The update could not be applied. The version you already had will open.";

// Carpeta de este exe, con '\' final. 0 si no se pudo leer.
static int CarpetaPropia(wchar_t *base)
{
    DWORD n = GetModuleFileNameW(NULL, base, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return 0;

    // Recortar el nombre del propio ejecutable: queda la carpeta con su '\'
    while (n > 0 && base[n - 1] != L'\\') --n;
    base[n] = L'\0';
    return 1;
}

// Arranca <base>\<app>\<app>.exe. <base> termina en '\'.
static int Arrancar(const wchar_t *base, const wchar_t *app)
{
    wchar_t carpeta[LARGO];
    wchar_t destino[LARGO];
    _snwprintf(carpeta, LARGO, L"%s%s",         base, app);
    _snwprintf(destino, LARGO, L"%s%s\\%s.exe", base, app, app);
    carpeta[LARGO - 1] = L'\0';
    destino[LARGO - 1] = L'\0';

    STARTUPINFOW si = { sizeof si };
    PROCESS_INFORMATION pi;

    // El 8º parámetro es el directorio de trabajo: equivale al /d del .cmd,
    // y es lo que hace que se comporte igual que un doble clic sobre el exe.
    if (!CreateProcessW(destino, NULL, NULL, NULL, FALSE,
        0, NULL, carpeta, &si, &pi))
    {
        wchar_t aviso[LARGO + 64];
        _snwprintf(aviso, LARGO + 64,
                   L"No se pudo arrancar:\n\n%s", destino);
        aviso[LARGO + 63] = L'\0';
        MessageBoxW(NULL, aviso, L"SheepGD", MB_ICONERROR | MB_OK);
        return 0;
    }

    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return 1;   // el lanzador se va; la app se queda
}

static int EsArchivo(const wchar_t *ruta)
{
    DWORD a = GetFileAttributesW(ruta);
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

static int EsCarpeta(const wchar_t *ruta)
{
    DWORD a = GetFileAttributesW(ruta);
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY);
}

// Renombra (carpeta o archivo, dentro del mismo disco) reintentando durante
// unos 20 s: justo después de cerrarse el programa, Windows o un antivirus
// pueden tener todavía algún archivo abierto.
static int MoverConReintentos(const wchar_t *de, const wchar_t *a)
{
    for (int i = 0; i < 40; i++)
    {
        if (MoveFileExW(de, a, 0)) return 1;
        Sleep(500);
    }
    return 0;
}

// Copia el lanzador nuevo sobre el de la raíz. Se copia (no se mueve) porque
// el del propio paquete es el que está en ejecución. El de la raíz se aparta
// con un nombre que LimpiarRestos reconoce. Si algo falla, queda el viejo.
static void ReemplazarLanzador(const wchar_t *paquete, const wchar_t *raiz,
                               const wchar_t *app, const wchar_t *sufijo)
{
    wchar_t nuevo[LARGO], actual[LARGO], temporal[LARGO], apartado[LARGO];
    _snwprintf(nuevo,    LARGO, L"%s%s.exe",       paquete, app);
    _snwprintf(actual,   LARGO, L"%s%s.exe",       raiz, app);
    _snwprintf(temporal, LARGO, L"%s%s.exe.nuevo", raiz, app);
    _snwprintf(apartado, LARGO, L"%s%s.exe%s",     raiz, app, sufijo);
    nuevo[LARGO - 1] = actual[LARGO - 1] = temporal[LARGO - 1] = apartado[LARGO - 1] = L'\0';

    if (!EsArchivo(nuevo)) return;
    if (!CopyFileW(nuevo, temporal, FALSE)) return;

    if (EsArchivo(actual) && !MoveFileExW(actual, apartado, MOVEFILE_REPLACE_EXISTING))
    {
        DeleteFileW(temporal);
        return;
    }

    if (!MoveFileExW(temporal, actual, 0))
    {
        MoveFileExW(apartado, actual, 0);
        DeleteFileW(temporal);
    }
}

static int AplicarActualizacion(DWORD pid, const wchar_t *raizArg)
{
    wchar_t paquete[LARGO];
    if (!CarpetaPropia(paquete)) return 1;

    // La raíz llega sin '\' final (salvo que sea la raíz de un disco).
    wchar_t raiz[LARGO];
    _snwprintf(raiz, LARGO - 1, L"%s", raizArg);
    raiz[LARGO - 2] = L'\0';
    size_t largo = wcslen(raiz);
    if (largo == 0) return 1;
    if (raiz[largo - 1] != L'\\') { raiz[largo] = L'\\'; raiz[largo + 1] = L'\0'; }

    // 1. Esperar a que el programa termine de cerrarse.
    HANDLE proceso = OpenProcess(SYNCHRONIZE, FALSE, pid);
    if (proceso != NULL)
    {
        WaitForSingleObject(proceso, 60000);
        CloseHandle(proceso);
    }

    wchar_t vieja[NUM_APPS][LARGO], apartada[NUM_APPS][LARGO], nueva[NUM_APPS][LARGO];
    wchar_t sufijo[32];
    _snwprintf(sufijo, 32, L".anterior.%lu", (unsigned long)GetTickCount());
    sufijo[31] = L'\0';

    for (int i = 0; i < NUM_APPS; i++)
    {
        wchar_t exeNuevo[LARGO];
        _snwprintf(vieja[i],    LARGO, L"%s%s",         raiz, APPS[i]);
        _snwprintf(apartada[i], LARGO, L"%s%s%s",       raiz, APPS[i], sufijo);
        _snwprintf(nueva[i],    LARGO, L"%s%s",         paquete, APPS[i]);
        _snwprintf(exeNuevo,    LARGO, L"%s%s\\%s.exe", paquete, APPS[i], APPS[i]);
        vieja[i][LARGO - 1] = apartada[i][LARGO - 1] = nueva[i][LARGO - 1] = exeNuevo[LARGO - 1] = L'\0';

        // Sin las dos apps nuevas, o sin la instalación de destino, no se toca nada.
        if (!EsArchivo(exeNuevo) || !EsCarpeta(vieja[i]))
        {
            MessageBoxW(NULL, AVISO_FALLO, L"SheepGD", MB_ICONERROR | MB_OK);
            Arrancar(raiz, L"SheepGDRequest");
            return 1;
        }
    }

    // 2. Apartar las carpetas viejas.
    int apartadas = 0;
    for (; apartadas < NUM_APPS; apartadas++)
    {
        while (!MoverConReintentos(vieja[apartadas], apartada[apartadas]))
        {
            if (MessageBoxW(NULL, AVISO_EN_USO, L"SheepGD", MB_RETRYCANCEL | MB_ICONWARNING) != IDRETRY)
            {
                for (int j = 0; j < apartadas; j++) MoveFileExW(apartada[j], vieja[j], 0);
                Arrancar(raiz, L"SheepGDRequest");
                return 1;
            }
        }
    }

    // 3. Poner las nuevas. Si una falla, se deshace todo.
    for (int puestas = 0; puestas < NUM_APPS; puestas++)
    {
        if (!MoverConReintentos(nueva[puestas], vieja[puestas]))
        {
            for (int j = 0; j < puestas; j++) MoveFileExW(vieja[j], nueva[j], 0);
            for (int j = 0; j < NUM_APPS; j++) MoveFileExW(apartada[j], vieja[j], 0);
            MessageBoxW(NULL, AVISO_FALLO, L"SheepGD", MB_ICONERROR | MB_OK);
            Arrancar(raiz, L"SheepGDRequest");
            return 1;
        }
    }

    // 4. Lanzadores de la raíz.
    for (int i = 0; i < NUM_APPS; i++) ReemplazarLanzador(paquete, raiz, APPS[i], sufijo);

    // 5. Abrir la versión nueva.
    return Arrancar(raiz, L"SheepGDRequest") ? 0 : 1;
}

int wmain(int argc, wchar_t **argv)
{
    if (argc >= 4 && wcscmp(argv[1], L"--aplicar-actualizacion") == 0)
    {
        DWORD pid = (DWORD)wcstoul(argv[2], NULL, 10);
        return AplicarActualizacion(pid, argv[3]);
    }

    wchar_t base[MAX_PATH];
    if (!CarpetaPropia(base)) return 1;
    return Arrancar(base, APP) ? 0 : 1;
}
