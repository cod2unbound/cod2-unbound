namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void SteamCases()
    {
        using var fixture = Fixtures("setup");
        foreach (var row in fixture.RootElement.GetProperty("steam").EnumerateArray())
            Check("Swift Steam depot progress " + row.GetProperty("language").GetString(), () =>
            {
                var language = Enum.Parse<SteamLanguage>(row.GetProperty("language").GetString()!, true);
                Sequence(row.GetProperty("ids").EnumerateArray().Select(value => value.GetInt32()),
                    SteamDownloadProgress.Depots(language).Select(depot => depot.Id));
                var progress = new SteamDownloadProgress(language);
                Equal(row.GetProperty("total").GetInt64(), progress.Total);
                Equal(row.GetProperty("room").GetInt64(), progress.RoomNeeded);
                var amounts = new List<long> { progress.Done };
                progress.File(50); amounts.Add(progress.Done);
                progress.Wrote(15_000_000); amounts.Add(progress.Done);
                progress.Depot(1); amounts.Add(progress.Done);
                progress.File(10); amounts.Add(progress.Done);
                progress.Depot(SteamDownloadProgress.Depots(language)[^1].Id); amounts.Add(progress.Done);
                progress.File(100); amounts.Add(progress.Done);
                progress.Wrote(long.MaxValue / 2); amounts.Add(progress.Done);
                progress.Finish(); amounts.Add(progress.Done);
                Sequence(row.GetProperty("amounts").EnumerateArray().Select(value => value.GetInt64()), amounts);
            });

        Check("Steam tick honors strict connect, sign-in, and unknown-write stall boundaries", () =>
        {
            var clock = new ManualClock();
            var connecting = new SteamAttempt("D:/Games/CoD2", SteamLanguage.English, clock);
            clock.Advance(60);
            Equal(SteamStopReason.None, connecting.Tick(0));
            clock.Advance(1);
            Equal(SteamStopReason.NoResponse, connecting.Tick(0));

            var signingIn = new SteamAttempt("D:/Games/CoD2", SteamLanguage.English, clock);
            signingIn.Receive(new(SteamSignal.Qr, Text: "login matrix"));
            clock.Advance(299);
            signingIn.Receive(new(SteamSignal.Qr, Text: "rotated matrix"));
            clock.Advance(1);
            Equal(SteamStopReason.None, signingIn.Tick(0));
            clock.Advance(1);
            Equal(SteamStopReason.SignInExpired, signingIn.Tick(0));

            var downloading = new SteamAttempt("D:/Games/CoD2", SteamLanguage.English, clock);
            downloading.Receive(new(SteamSignal.SignedIn));
            downloading.Receive(new(SteamSignal.File, Text: "D:\\Games\\CoD2\\main\\iw_01.iwd", Percent: 25));
            Equal("main/iw_01.iwd", downloading.State.CurrentFile);
            clock.Advance(900);
            Equal(SteamStopReason.None, downloading.Tick(0));
            clock.Advance(1);
            Equal(SteamStopReason.Stalled, downloading.Tick(0));
        });
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance(int seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
