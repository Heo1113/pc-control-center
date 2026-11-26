using Iron.App.Presentation;
using Iron.ProcessIdentity;

namespace Iron.App.Tests;

/// <summary>
/// Process identity in Stage 1: the name cell shows the icon (letter mark for mock rows) and the identified program name,
/// the tooltip keeps role and basis; mock rows only, no real process lookup, data / search / sort unchanged.
/// </summary>
public class ProcessIdentityTests
{
    [Fact]
    public void EveryMockRowGetsAMockIdentityAndNameSearchSortStayTheSame()
    {
        var rig = Rig.Create();
        rig.Readings(2);
        var p = rig.Vm.Processes;
        Assert.NotEmpty(p.Rows);
        foreach (var row in p.Rows)
        {
            Assert.NotNull(row.Identity);
            Assert.Equal(IdentitySource.Mock, row.Identity!.Source);
            Assert.Contains("Stage 1 모의", row.Identity.Text, StringComparison.Ordinal);
            Assert.Equal(row.Reading.Name, row.Name); // the row's data keeps the executable name
            Assert.Equal(row.Reading.Name, row.Identity.ImageName);
            Assert.False(string.IsNullOrWhiteSpace(row.Identity.Name));
            Assert.True(row.Identity.Detail is null || row.Identity.Detail.EndsWith(row.Reading.Name, StringComparison.Ordinal)); // the exe name stays visible
            Assert.Contains("근거: ", row.Identity.Text, StringComparison.Ordinal);
            Assert.Null(row.Identity.Icon); // no file is read for a mock row: the cell shows a letter mark
            Assert.Matches("^[A-Z0-9?]$", row.Identity.Monogram);
        }

        var dwm = p.Rows.Single(r => r.Name == "dwm.exe").Identity!;
        Assert.Equal("데스크톱 창 관리자", dwm.Name);
        Assert.Equal("dwm.exe", dwm.Detail);
        Assert.Equal("D", dwm.Monogram);
        var webview = p.Rows.Single(r => r.Name == "msedgewebview2.exe").Identity!;
        Assert.Equal("공유 호스트 · msedgewebview2.exe", webview.Detail); // shared host is marked in the cell itself

        // Order is still by dedicated memory, then name; search still matches the executable name.
        var order = p.Rows.Select(r => r.Reading).ToList();
        Assert.Equal(order.OrderByDescending(r => r.DedicatedGb).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => r.Pid), order.Select(r => r.Pid));
        p.Search = "chrome";
        Assert.All(p.Rows, r => Assert.Contains("chrome", r.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MockSharedHostShowsNoticeAndNothingIsConfirmed()
    {
        var id = IdentityComposer.ComposeMock(7716, "msedgewebview2.exe", @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\msedgewebview2.exe --type=gpu-process");
        Assert.True(id.IsSharedHost);
        var text = id.ToToolTipText();
        Assert.Contains(ProcessIdentityInfo.SharedHostNotice, text, StringComparison.Ordinal);
        Assert.DoesNotContain("확인 불가", text.Replace(ProcessIdentityInfo.SharedHostNotice, ""), StringComparison.Ordinal);
        Assert.Equal(IdentityConfidence.Inferred, id.Title.Confidence);
        Assert.All(id.Hosted, h => Assert.NotEqual(IdentityConfidence.Confirmed, h.Confidence));
    }

    [Fact]
    public void Stage1CodeNeverReachesTheRealProcessResolver()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Iron.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        var forbidden = new[] { "WindowsProcessQuery", "ProcessIdentityResolver", "IdentityResolverFactory", "NativeProcessInterop", "OpenProcess" };
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!, "src", "Iron.App"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal)) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (var word in forbidden)
            {
                Assert.False(text.Contains(word, StringComparison.Ordinal), $"{Path.GetFileName(file)} uses {word} (real process lookup is Stage 2)");
            }
        }
    }

    [Fact]
    public void MockProviderIsBounded()
    {
        var provider = new MockProcessIdentityProvider();
        for (var i = 0; i < MockProcessIdentityProvider.Capacity * 3; i++)
        {
            provider.Identify(new ProcessReading(i, "app.exe", "RTX 5080", 0, 0, 0, "3D", "app.exe --n " + i));
        }

        var again = provider.Identify(new ProcessReading(1, "app.exe", "RTX 5080", 0, 0, 0, "3D", "app.exe --n 1"));
        Assert.Equal(IdentitySource.Mock, again.Source);
    }
}
