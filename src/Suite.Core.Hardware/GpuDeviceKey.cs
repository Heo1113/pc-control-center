using System.Globalization;
using LocalAiPerf.Core.Hardware;

namespace Suite.Core.Hardware;

/// <summary>
/// A stable identity for one GPU across readings and restarts: the NVML UUID when known, else the PCI location, else the
/// NVML index (which can change when cards are added, removed or the driver enumerates them differently).
/// </summary>
public static class GpuDeviceKey
{
    public static string Of(GpuSample gpu) => Of(gpu.DeviceUuid, gpu.PciAddress, gpu.Index);

    public static string Of(GpuInfo gpu) => Of(gpu.DeviceUuid, gpu.PciAddress, gpu.Index);

    public static string Of(string? uuid, GpuPciAddress? pci, int index)
    {
        if (!string.IsNullOrWhiteSpace(uuid))
        {
            return "uuid:" + uuid.Trim();
        }

        if (pci is { } p)
        {
            return string.Create(CultureInfo.InvariantCulture, $"pci:{p.Domain ?? 0:x4}:{p.Bus:x2}:{p.Device:x2}.{p.Function:x}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"index:{index}");
    }
}
