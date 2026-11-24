using System.Runtime.InteropServices;

// kernel32.dll is loaded from System32 only, never from the application folder.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
