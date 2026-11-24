using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LocalAiPerf.Core.Hardware;

namespace PcControlCenter.GpuMemory.Windows;

/// <summary>Queries the WDDM adapter selected by the DXGI LUID. D3DKMT reports no PCI segment number.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class AdapterPciLocation
{
    public static GpuPciAddress? Read(uint low, int high)
    {
        var open = new OpenAdapter { Low = low, High = high };
        if (Open(ref open) != 0) return null;
        try
        {
            Address address = default;
            const int adapterAddress = 6; // KMTQAITYPE_ADAPTERADDRESS
            var query = new AdapterQuery { Adapter = open.Adapter, Type = adapterAddress, Data = &address, Size = (uint)sizeof(Address) };
            return Query(ref query) == 0 && address.Bus <= 255 && address.Device <= 31 && address.Function <= 7
                ? new GpuPciAddress(null, address.Bus, address.Device, address.Function) : null;
        }
        finally
        {
            Close(ref open.Adapter);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenAdapter { public uint Low; public int High; public uint Adapter; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Address { public uint Bus, Device, Function; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterQuery { public uint Adapter; public int Type; public Address* Data; public uint Size; }

    [LibraryImport("gdi32.dll", EntryPoint = "D3DKMTOpenAdapterFromLuid")]
    private static partial int Open(ref OpenAdapter adapter);
    [LibraryImport("gdi32.dll", EntryPoint = "D3DKMTQueryAdapterInfo")]
    private static partial int Query(ref AdapterQuery query);
    [LibraryImport("gdi32.dll", EntryPoint = "D3DKMTCloseAdapter")]
    private static partial int Close(ref uint adapter);
}
