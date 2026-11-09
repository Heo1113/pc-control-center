using WinDiag.Collection;
using WinDiag.Core.Events;
using WinDiag.Core.Privacy;
using WinDiag.Storage;

namespace WinDiag.Tests.Collection;

public class EventReclassifierTests
{
    [Fact]
    public void Events_stored_with_older_rules_are_reclassified_once_and_demo_events_are_left_alone()
    {
        using var dir = new TempDir();
        using var store = DiagnosticsStore.Open(dir.File("wdl.db"));
        var schannel = Ev.Make("Schannel", 36874, level: 2,
            data: [("CallerProcessId", "7648"), ("CallerProcessImageName", "svchost[TermService]"), ("Protocol", "TLS 1.0")]);
        var vss = Ev.Make("VSS", 13, channel: "Application", message: "… [0x8007045b, 시스템을 종료하고 있습니다.]");
        var demo = Ev.Make("Schannel", 36874, level: 2, message: "[가상] 테스트");

        // What the version 1 catalogue produced for these: uncatalogued errors.
        static EventClassification Old(RawEvent e) => new(EventCategories.Other, DiagSeverity.Error, $"{e.Provider} 이벤트 {e.EventId}", null, null,
            "old", EventClassifier.GroupKey(EventCategories.Other, e.Provider, e.EventId, "old"), [], false);
        store.InsertEvents([
            new NewEvent(schannel, Old(schannel), EventSources.History, true),
            new NewEvent(vss, Old(vss), EventSources.Live, true),
            new NewEvent(demo, Old(demo) with { Title = "[가상] 데모" }, EventSources.Virtual, true),
        ]);
        store.SetMeta(EventReclassifier.MetaKey, "1");

        var result = EventReclassifier.RunIfOutdated(store, EventClassifier.Default, PrivacyMasker.Generic);
        Assert.Equal(new EventReclassifier.Result(2, 2), result);
        var events = store.QueryEvents(new EventQuery { Limit = 10 });
        var tls = events.Single(e => e.Provider == "Schannel" && e.Source != EventSources.Virtual);
        Assert.Equal(DiagSeverity.Warning, tls.Severity);
        Assert.StartsWith("TLS 연결 실패 (Schannel)", tls.Title, StringComparison.Ordinal);
        Assert.Equal(DiagSeverity.Info, events.Single(e => e.Provider == "VSS").Severity);
        Assert.Equal("[가상] 데모", events.Single(e => e.Source == EventSources.Virtual).Title);
        Assert.Equal(EventCatalog.Version, store.GetMetaLong(EventReclassifier.MetaKey));

        Assert.Null(EventReclassifier.RunIfOutdated(store, EventClassifier.Default, PrivacyMasker.Generic));
    }
}
