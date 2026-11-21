using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PcControlCenter.GpuMemory.Windows;
using PcControlCenter.Presentation;

namespace PcControlCenter.App;

/// <summary>
/// The GPU memory page's executable icons, read on one background STA thread (the shell's icon lookup may need COM) and
/// cached: by process (PID and name) and by executable path. A process whose path cannot be read (protected, another
/// user's, gone) or whose path does not end in its name (the PID was reused) gets the generic program icon. The path is
/// only the cache key; it is not shown or logged.
/// </summary>
internal sealed class ProcessIconService : IProcessIcons, IDisposable
{
    private const int MaxProcesses = 1024;
    private readonly Dispatcher _dispatcher;
    private readonly BlockingCollection<(int Pid, string Name)> _queue = new(boundedCapacity: 4096);
    private readonly ConcurrentDictionary<(int Pid, string Name), ImageSource?> _byProcess = new();
    private readonly ConcurrentDictionary<string, ImageSource?> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(int Pid, string Name), byte> _pending = new();
    private readonly Lazy<ImageSource?> _generic = new(() => Load(".exe", genericForExtension: true));
    private readonly Thread _worker;

    public ProcessIconService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _worker = new Thread(Run) { IsBackground = true, Name = "Process icons" };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    public event Action? Changed;

    public object? Icon(int pid, string name)
    {
        if (pid <= 4 || string.IsNullOrEmpty(name))
        {
            return null; // the kernel and the idle process have no executable
        }

        if (_byProcess.TryGetValue((pid, name), out var icon))
        {
            return icon ?? _generic.Value;
        }

        if (_pending.TryAdd((pid, name), 0) && !_queue.TryAdd((pid, name)))
        {
            _pending.TryRemove((pid, name), out _);
        }

        return _generic.Value; // until the real one is read
    }

    private void Run()
    {
        foreach (var (pid, name) in _queue.GetConsumingEnumerable())
        {
            ImageSource? icon = null;
            try
            {
                if (ProcessIconSource.ImagePath(pid) is { } path && string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
                {
                    icon = _byPath.GetOrAdd(path, p => Load(p, genericForExtension: false));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                icon = null; // the generic icon is shown instead
            }

            if (_byProcess.Count >= MaxProcesses)
            {
                _byProcess.Clear(); // processes come and go; the path cache keeps the images
            }

            _byProcess[(pid, name)] = icon;
            _pending.TryRemove((pid, name), out _);
            if (icon is not null)
            {
                _dispatcher.BeginInvoke(() => Changed?.Invoke(), DispatcherPriority.Background);
            }
        }
    }

    private static ImageSource? Load(string path, bool genericForExtension)
    {
        var handle = ProcessIconSource.SmallIcon(path, genericForExtension);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
            source.Freeze();
            return source;
        }
        finally
        {
            ProcessIconSource.DestroyIcon(handle);
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}
