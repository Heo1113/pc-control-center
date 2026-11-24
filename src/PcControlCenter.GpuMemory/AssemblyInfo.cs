using System.Runtime.InteropServices;

// pdh.dll, dxgi.dll, iphlpapi.dll and kernel32.dll are loaded from System32 only, never from the application folder.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
