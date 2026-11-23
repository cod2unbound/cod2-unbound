using System.Text;
using CoD2Unbound.Core;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void NewsCases()
    {
        Check("News destination survives parsing, storage and presentation", () =>
        {
            var now = DateTimeOffset.Parse("2026-11-23T14:02:00Z");
            var input = Encoding.UTF8.GetBytes("""
                {"items":[{"date":"2026-11-22","title":"Setups","open":"configs","link":"https://github.com/example/release"},
                          {"date":"2026-11-21","title":"Elsewhere","link":"https://github.com/example/news"},
                          {"date":"2026-11-20","title":"An announcement","open":"unknown"}],
                 "next":{"title":"Next","open":"servers"}}
                """);
            var news = NewsFeed.Parse(input, now, TimeZoneInfo.Utc);
            var library = LauncherLibrary.Read(new LauncherLibrary { News = news.Items, NewsNext = news.Next }.Write().AsMemory());
            var rows = NewsFeed.Home("0.5.0", [], library.News, library.NewsNext, rows: 12);
            Equal(NewsDestination.Configs, rows.Single(row => row.Title == "Setups").Destination);
            Equal(NewsDestination.Browser, rows.Single(row => row.Title == "Elsewhere").Destination);
            Equal(NewsDestination.None, rows.Single(row => row.Title == "An announcement").Destination);
            Equal(NewsDestination.Updates, rows.Single(row => row.Version == "0.5.0").Destination);
            Equal(NewsDestination.Servers, rows.Last().Destination);
        });
        Check("See all reserves the last row for next and shows dated news newest first", () =>
        {
            var days = Enumerable.Range(1, 12).Select(day => new NewsDispatch($"NOV {day}", $"Day {day}", "",
                Date: new DateTimeOffset(2026, 11, day, 0, 0, 0, TimeSpan.Zero))).ToArray();
            var library = LauncherLibrary.Read(new LauncherLibrary { News = [.. days] }.Write().AsMemory());
            Equal(12, library.News.Length);
            var rows = NewsFeed.Home("0.5.0", [], library.News, new("NEXT", "Next", ""), rows: 12);
            Equal(12, rows.Length);
            Equal("Day 12", rows[0].Title);
            Equal("Day 2", rows[^2].Title);
            Equal("Next", rows[^1].Title);
        });
        Check("Release feed keeps notes for installed and offered versions across launches", () =>
        {
            var feeds = ReleaseNotes.Parse("""
                [{"tag_name":"v0.6.0","body":"### New\n- New thing. Offered detail."},
                 {"tag_name":"v0.5.0","body":"### Fixed\n- Old thing. Installed detail."}]
                """u8);
            var library = LauncherLibrary.Read(new LauncherLibrary { Releases = feeds.Details }.Write().AsMemory());
            Equal(new ReleaseNoteItem("Fixed", "Old thing", "Installed detail."), library.Releases.Single(row => row.Version == "0.5.0").Notes.Single());
            Equal("New thing", library.Releases.Single(row => row.Version == "0.6.0").Notes.Single().Title);
            Equal(0, LauncherLibrary.Read("{}"u8.ToArray()).Releases.Length);
        });
        Check("An automatic check can nominate a version first found in the foreground", () =>
        {
            using var fixture = Fixtures("settings");
            var row = fixture.RootElement.GetProperty("release")[1];
            var clock = new AdvancingClock();
            var releases = new TestReleases([.. Encoding.UTF8.GetBytes(row.GetProperty("json").GetString()!)]);
            var updates = new LauncherUpdates(releases, clock, "0.0.1",
                (version, assets) => ReleasePackage.Offer(version, $"CoD2-Unbound-{version}-macos-arm64.zip", assets), TimeZoneInfo.Utc);
            var manual = updates.CheckAsync(new(), automatic: false).GetAwaiter().GetResult();
            Equal<string?>(null, manual.NotifyVersion);
            Equal(UpdatePhase.Offered, manual.State.Phase);
            var automatic = updates.CheckAsync(manual.Library, automatic: true).GetAwaiter().GetResult();
            Equal(manual.State.AvailableVersion, automatic.NotifyVersion);
        });
        Check("Update notification is automatic, background, idle, available and once per version", () =>
        {
            var saved = LauncherLibrary.Read(new LauncherLibrary { NotifiedUpdates = ["0.6.0"] }.Write().AsMemory());
            bool Show(string? version = "0.7.0", bool foreground = false, bool running = false, bool available = true) =>
                UpdateNotification.ShouldShow(version, foreground, running, available, saved.NotifiedUpdates);
            Equal(true, Show());
            Equal(false, Show(version: null));
            Equal(false, Show(version: "0.6.0"));
            Equal(false, Show(foreground: true));
            Equal(false, Show(running: true));
            Equal(false, Show(available: false));
            Equal("update-0.7.0", UpdateNotification.Tag("0.7.0"));
            Equal(false, UpdateNotification.IsVersion("0.7.0&action=other"));
        });
    }
}
