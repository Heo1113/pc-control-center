using System.Runtime.Versioning;
using static PcControlCenter.GpuMemory.Windows.NativeMethods;

namespace PcControlCenter.GpuMemory.Windows;

/// <summary>
/// Adapter names and capacities from DXGI (IDXGIFactory1::EnumAdapters1, IDXGIAdapter1::GetDesc1), keyed by the LUID the
/// performance counters use. No device is created; nothing is changed. Adapters are paired with counters by LUID only.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class DxgiAdapters
{
    // COM vtable slots: IUnknown 0-2, IDXGIObject 3-6, IDXGIFactory 7-11, IDXGIFactory1::EnumAdapters1 = 12.
    private const int ReleaseSlot = 2;
    private const int EnumAdapters1Slot = 12;

    // IDXGIAdapter 7-9, IDXGIAdapter1::GetDesc1 = 10.
    private const int GetDesc1Slot = 10;

    public static IReadOnlyList<GpuAdapterInfo> Enumerate(out string? error)
    {
        var adapters = new List<GpuAdapterInfo>();
        var status = CreateDXGIFactory1(IidIdxgiFactory1, out var factory);
        if (status < 0 || factory == 0)
        {
            error = $"DXGI 어댑터 목록을 만들 수 없습니다 (HRESULT 0x{status:X8}). 어댑터 이름 없이 LUID로만 표시합니다.";
            return adapters;
        }

        error = null;
        try
        {
            var enumAdapters = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(factory, EnumAdapters1Slot);
            for (uint index = 0; index < 64; index++)
            {
                nint adapter;
                var hr = enumAdapters(factory, index, &adapter);
                if (hr == DxgiErrorNotFound)
                {
                    break;
                }

                if (hr < 0 || adapter == 0)
                {
                    error = $"DXGI 어댑터 {index}번을 읽지 못했습니다 (HRESULT 0x{hr:X8}).";
                    break;
                }

                try
                {
                    var getDesc = (delegate* unmanaged[Stdcall]<nint, DxgiAdapterDesc1*, int>)Slot(adapter, GetDesc1Slot);
                    DxgiAdapterDesc1 desc;
                    if (getDesc(adapter, &desc) >= 0)
                    {
                        adapters.Add(new GpuAdapterInfo(
                            GpuLuid.From(unchecked((uint)desc.AdapterLuidHighPart), desc.AdapterLuidLowPart),
                            Text(new ReadOnlySpan<char>(desc.Description, 128)),
                            (int)desc.VendorId,
                            (long)Math.Min((ulong)desc.DedicatedVideoMemory, long.MaxValue),
                            (long)Math.Min((ulong)desc.SharedSystemMemory, long.MaxValue),
                            (desc.Flags & DxgiAdapterFlagSoftware) != 0));
                    }
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }

        return adapters;
    }

    internal static string Text(ReadOnlySpan<char> buffer)
    {
        var end = buffer.IndexOf('\0');
        return (end >= 0 ? buffer[..end] : buffer).ToString().Trim();
    }

    private static nint Slot(nint comObject, int slot) => (*(nint**)comObject)[slot];

    private static void Release(nint comObject)
    {
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)Slot(comObject, ReleaseSlot);
        _ = release(comObject);
    }
}
