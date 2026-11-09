using WinDiag.Core;

namespace WinDiag.Tests.Core;

public class DataFolderLockTests
{
    [Fact]
    public async Task One_holder_per_data_folder_until_it_is_released()
    {
        using var dir = new TempDir();
        var a = Directory.CreateDirectory(Path.Combine(dir.Path, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(dir.Path, "b")).FullName;
        using (var first = DataFolderLock.TryAcquire(a))
        {
            Assert.NotNull(first);
            Assert.Null(DataFolderLock.TryAcquire(a));
            Assert.Null(await Task.Run(() => DataFolderLock.TryAcquire(a), TestContext.Current.CancellationToken)); // not tied to a thread
            using var other = DataFolderLock.TryAcquire(b); // another data folder is independent
            Assert.NotNull(other);
        }

        using var again = DataFolderLock.TryAcquire(a);
        Assert.NotNull(again);
        Assert.True(File.Exists(Path.Combine(a, DataFolderLock.FileName)));
    }

    [Fact]
    public void A_missing_folder_is_an_error_not_a_lock_held_elsewhere()
    {
        using var dir = new TempDir();
        Assert.ThrowsAny<IOException>(() => DataFolderLock.TryAcquire(Path.Combine(dir.Path, "missing")));
    }
}
