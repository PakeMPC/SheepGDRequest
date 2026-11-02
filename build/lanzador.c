// lanzador.c — Arranca <carpeta de este exe>\APP\APP.exe
//
// El nombre de la app llega por -D al compilar, así que el MISMO archivo
// genera los dos lanzadores. Todo en wchar: los nombres de usuario con
// acentos y las rutas con espacios son la norma en Windows, no la excepción.

#include <windows.h>
#include <stdio.h>

#ifndef APP
#define APP L"SheepGDRequest"
#endif

int wmain(void)
{
    wchar_t base[MAX_PATH];
    DWORD n = GetModuleFileNameW(NULL, base, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return 1;

    // Recortar el nombre del propio ejecutable: queda la carpeta con su '\'
    while (n > 0 && base[n - 1] != L'\\') --n;
    base[n] = L'\0';

    wchar_t carpeta[MAX_PATH * 2];
    wchar_t destino[MAX_PATH * 2];
    _snwprintf(carpeta, MAX_PATH * 2, L"%s%s",         base, APP);
    _snwprintf(destino, MAX_PATH * 2, L"%s%s\\%s.exe", base, APP, APP);
    carpeta[MAX_PATH * 2 - 1] = L'\0';
    destino[MAX_PATH * 2 - 1] = L'\0';

    STARTUPINFOW si = { sizeof si };
    PROCESS_INFORMATION pi;

    // El 8º parámetro es el directorio de trabajo: equivale al /d del .cmd,
    // y es lo que hace que se comporte igual que un doble clic sobre el exe.
    if (!CreateProcessW(destino, NULL, NULL, NULL, FALSE,
        0, NULL, carpeta, &si, &pi))
    {
        wchar_t aviso[MAX_PATH * 2 + 64];
        _snwprintf(aviso, MAX_PATH * 2 + 64,
                   L"No se pudo arrancar:\n\n%s", destino);
        aviso[MAX_PATH * 2 + 63] = L'\0';
        MessageBoxW(NULL, aviso, L"SheepGD", MB_ICONERROR | MB_OK);
        return 1;
    }

    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return 0;   // el lanzador se va; la app se queda
}
