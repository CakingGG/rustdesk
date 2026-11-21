#include <windows.h>
#include <sddl.h>

extern "C" HANDLE LaunchProcessWin(LPCWSTR, DWORD, BOOL, BOOL, DWORD *);

extern "C" LONG set_phone_display_mode(LPCWSTR name, DWORD width, DWORD height)
{
    DEVMODEW mode = {};
    mode.dmSize = sizeof(mode);
    if (!EnumDisplaySettingsW(name, ENUM_CURRENT_SETTINGS, &mode))
        return DISP_CHANGE_FAILED;
    LONG right = 0;
    bool overlaps = false;
    for (DWORD i = 0; ; ++i) {
        DISPLAY_DEVICEW adapter = {};
        adapter.cb = sizeof(adapter);
        if (!EnumDisplayDevicesW(NULL, i, &adapter, 0))
            break;
        if (!(adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) ||
            _wcsicmp(name, adapter.DeviceName) == 0)
            continue;
        DEVMODEW other = {};
        other.dmSize = sizeof(other);
        if (!EnumDisplaySettingsW(adapter.DeviceName, ENUM_CURRENT_SETTINGS, &other))
            continue;
        RECT occupied = {other.dmPosition.x, other.dmPosition.y,
            other.dmPosition.x + (LONG)other.dmPelsWidth, other.dmPosition.y + (LONG)other.dmPelsHeight};
        RECT proposed = {mode.dmPosition.x, mode.dmPosition.y,
            mode.dmPosition.x + (LONG)width, mode.dmPosition.y + (LONG)height};
        RECT intersection;
        overlaps |= IntersectRect(&intersection, &occupied, &proposed) != FALSE;
        if (occupied.right > right)
            right = occupied.right;
    }
    if (!overlaps && mode.dmPelsWidth == width && mode.dmPelsHeight == height)
        return DISP_CHANGE_SUCCESSFUL;
    mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
    mode.dmPelsWidth = width;
    mode.dmPelsHeight = height;
    if (overlaps) {
        mode.dmFields |= DM_POSITION;
        mode.dmPosition.x = right;
        mode.dmPosition.y = 0;
    }
    return ChangeDisplaySettingsExW(name, &mode, NULL, 0, NULL);
}

// The picker enumerates and launches applications in the interactive user's shell.
extern "C" HANDLE start_phone_workspace_picker(LPCWSTR command, LPCWSTR eventName, HANDLE *stopEvent)
{
    DWORD session, tokenPid = 0;
    if (wcslen(command) >= MAX_PATH) {
        SetLastError(ERROR_FILENAME_EXCED_RANGE);
        return NULL;
    }
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &session))
        return NULL;
    PSECURITY_DESCRIPTOR descriptor = NULL;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
            L"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00100000;;;IU)",
            SDDL_REVISION_1, &descriptor, NULL))
        return NULL;
    SECURITY_ATTRIBUTES attributes = {sizeof(attributes), descriptor, FALSE};
    HANDLE event = CreateEventW(&attributes, TRUE, FALSE, eventName);
    LocalFree(descriptor);
    if (!event)
        return NULL;
    HANDLE process = LaunchProcessWin(command, session, TRUE, TRUE, &tokenPid);
    if (!process) {
        DWORD error = GetLastError();
        CloseHandle(event);
        SetLastError(error);
        return NULL;
    }
    *stopEvent = event;
    return process;
}
