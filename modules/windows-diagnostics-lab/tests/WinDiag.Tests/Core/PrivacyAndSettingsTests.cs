using System.Security.Cryptography;
using System.Text.Json;
using WinDiag.Core.Events;
using WinDiag.Core.Privacy;
using WinDiag.Core.Settings;

namespace WinDiag.Tests.Core;

public class PrivacyMaskerTests
{
    private readonly PrivacyMasker _masker = new("kimtester", "DESKTOP-TEST01", @"C:\Users\kimtester");

    [Theory]
    [InlineData(@"C:\Users\kimtester\Games\game.exe", @"C:\Users\<사용자>\Games\game.exe")]
    [InlineData(@"D:\Users\other.person\AppData\x.dll", @"D:\Users\<사용자>\AppData\x.dll")]
    [InlineData(@"\Device\HarddiskVolume3\Users\someone\a.txt", @"\Device\HarddiskVolume3\Users\<사용자>\a.txt")]
    [InlineData(@"C:\Users\Public\Documents\a.txt", @"C:\Users\Public\Documents\a.txt")]
    [InlineData("SID S-1-5-21-1111111111-2222222222-3333333333-1001 logged", "SID S-1-5-21-<SID> logged")]
    [InlineData("account S-1-5-18 is SYSTEM", "account S-1-5-18 is SYSTEM")]
    [InlineData("mail kim.tester@example.com now", "mail <이메일> now")]
    [InlineData(@"on computer DESKTOP-TEST01 by DESKTOP-TEST01\kimtester", @"on computer <컴퓨터> by <컴퓨터>\<사용자>")]
    [InlineData("user kimtester signed in", "user <사용자> signed in")]
    [InlineData("api_key=abc123 token: zzz", "api_key=[REDACTED] token=[REDACTED]")]
    [InlineData("driver 32.0.15.8115 on PCI\\VEN_10DE&DEV_2C02", "driver 32.0.15.8115 on PCI\\VEN_10DE&DEV_2C02")]
    public void Masks_personal_identifiers_and_keeps_diagnostic_values(string input, string expected)
        => Assert.Equal(expected, _masker.Mask(input));

    [Fact]
    public void Common_account_names_are_not_replaced_in_sentences()
    {
        var masker = new PrivacyMasker("User", "PC1", null);
        Assert.Equal("The user profile service failed", masker.Mask("The user profile service failed"));
        Assert.Equal(@"C:\Users\<사용자>\x", masker.Mask(@"C:\Users\User\x"));
    }

    [Fact]
    public void Masks_computer_name_of_imported_events()
    {
        var masked = PrivacyMasker.Generic.Mask(@"host OTHER-PC.corp.example and OTHER-PC\alice", "OTHER-PC.corp.example");
        Assert.Equal(@"host <컴퓨터> and <컴퓨터>\<사용자>", masked);
    }

    [Fact]
    public void Device_instance_serials_are_masked_for_exports()
    {
        var text = @"USB\VID_046D&PID_C52B\5&2A3B4C&0&1 and PCI\VEN_10DE&DEV_2C02&SUBSYS_0&REV_A1\4&1a2b3c4d&0&0008";
        Assert.Equal(@"USB\VID_046D&PID_C52B\<인스턴스> and PCI\VEN_10DE&DEV_2C02&SUBSYS_0&REV_A1\<인스턴스>", PrivacyMasker.MaskDeviceInstances(text));
    }

    [Fact]
    public void Masking_an_event_masks_message_and_every_data_value()
    {
        var e = Fixtures.Event("app-error-1000.xml") with { Message = @"crash in C:\Users\kimtester\Games\game.exe" };
        var masked = _masker.Mask(e);
        Assert.DoesNotContain("kimtester", masked.Message);
        Assert.DoesNotContain(masked.Data, d => d.Value.Contains("kimtester", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("game.exe", masked.Get("AppName"));
    }
}

public class SettingsTests
{
    [Fact]
    public void Normalize_clamps_values_removes_security_and_keeps_system()
    {
        var settings = new AppSettings();
        settings.Collection.Channels = [new ChannelSetting { Name = "Security" }, new ChannelSetting { Name = "Application" }];
        settings.Collection.PollSeconds = 0;
        settings.Sensors.IntervalSeconds = 999;
        var notes = settings.Normalize();
        Assert.DoesNotContain(settings.Collection.Channels, c => c.Name == "Security");
        Assert.Equal("System", settings.Collection.Channels[0].Name);
        Assert.Equal(2, settings.Collection.PollSeconds);
        Assert.Equal(60, settings.Sensors.IntervalSeconds);
        Assert.True(notes.Count >= 3);
    }

    [Fact]
    public void Broken_settings_file_is_kept_aside_and_defaults_are_used()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{ not json");
        var result = new SettingsStore(path).Load();
        Assert.True(result.WasCreated);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(dir.Path, "settings.json.invalid-*"));
        Assert.NotEmpty(result.Notes);
    }

    [Fact]
    public void Save_and_load_round_trip()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        var settings = new AppSettings();
        settings.Notifications.MinimumSeverity = WinDiag.Core.Events.DiagSeverity.Critical;
        settings.Collection.HistoryDays = 90;
        store.Save(settings);
        var loaded = store.Load().Settings;
        Assert.Equal(WinDiag.Core.Events.DiagSeverity.Critical, loaded.Notifications.MinimumSeverity);
        Assert.Equal(90, loaded.Collection.HistoryDays);
    }
}

/// <summary>
/// PC Control Center: C used to compile project A's files from its vendor copy (checked here against a manifest). In the
/// integrated repository C references A's single copy, so this test now checks that the sensor types C uses come from
/// A's assemblies and are not compiled into C a second time.
/// </summary>
public class VendorIntegrityTests
{
    [Fact]
    public void Project_A_code_is_referenced_once_not_compiled_into_C()
    {
        Assert.Equal("LocalAiPerf.Core", typeof(LocalAiPerf.Core.Hardware.HardwareSnapshot).Assembly.GetName().Name);
        Assert.Equal("LocalAiPerf.Telemetry", typeof(LocalAiPerf.Telemetry.HardwareMonitor).Assembly.GetName().Name);
        foreach (var assembly in new[] { typeof(WinDiag.Core.AppPaths).Assembly, typeof(WinDiag.Collection.DiagnosticsEngine).Assembly, typeof(WinDiag.Storage.DiagnosticsStore).Assembly })
        {
            Assert.DoesNotContain(assembly.GetTypes(), t => t.Namespace?.StartsWith("LocalAiPerf.", StringComparison.Ordinal) == true);
        }
    }
}
