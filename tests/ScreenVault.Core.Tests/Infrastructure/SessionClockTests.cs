using ScreenVault.Core.Infrastructure;
using Xunit;

namespace ScreenVault.Core.Tests.Infrastructure;

public sealed class SessionClockTests
{
    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        public long Timestamp { get; set; } = 1000;

        public double SecondsBetween(long startTimestamp, long endTimestamp)
        {
            return (endTimestamp - startTimestamp) / 1000.0;
        }

        public void AdvanceSeconds(double seconds)
        {
            Timestamp += (long)(seconds * 1000.0);
            UtcNow = UtcNow.AddSeconds(seconds);
        }
    }

    [Fact]
    public void InitialState_IsZeroAndNotRunning()
    {
        var clock = new FakeClock();
        var sessionClock = new SessionClock(clock);

        Assert.False(sessionClock.IsRunning);
        Assert.Equal(TimeSpan.Zero, sessionClock.Elapsed);
        Assert.Equal(TimeSpan.Zero, sessionClock.CurrentPartElapsed);
    }

    [Fact]
    public void StartSpan_AdvancesElapsed()
    {
        var clock = new FakeClock();
        var sessionClock = new SessionClock(clock);

        sessionClock.StartSpan();
        Assert.True(sessionClock.IsRunning);

        clock.AdvanceSeconds(5.5);
        Assert.Equal(5.5, sessionClock.Elapsed.TotalSeconds, precision: 2);
        Assert.Equal(5.5, sessionClock.CurrentPartElapsed.TotalSeconds, precision: 2);
    }

    [Fact]
    public void StopSpan_PausesElapsedAccumulation()
    {
        var clock = new FakeClock();
        var sessionClock = new SessionClock(clock);

        sessionClock.StartSpan();
        clock.AdvanceSeconds(10);
        sessionClock.StopSpan();

        Assert.False(sessionClock.IsRunning);
        Assert.Equal(10, sessionClock.Elapsed.TotalSeconds, precision: 2);
        Assert.Equal(TimeSpan.Zero, sessionClock.CurrentPartElapsed);

        // Advance while paused
        clock.AdvanceSeconds(30);
        Assert.Equal(10, sessionClock.Elapsed.TotalSeconds, precision: 2);

        // Resume second part
        sessionClock.StartPartSpan();
        Assert.True(sessionClock.IsRunning);
        clock.AdvanceSeconds(5);

        Assert.Equal(15, sessionClock.Elapsed.TotalSeconds, precision: 2);
        Assert.Equal(5, sessionClock.CurrentPartElapsed.TotalSeconds, precision: 2);
    }

    [Fact]
    public void Reset_ClearsAllAccumulatedTime()
    {
        var clock = new FakeClock();
        var sessionClock = new SessionClock(clock);

        sessionClock.StartSpan();
        clock.AdvanceSeconds(20);
        sessionClock.StopSpan();

        sessionClock.Reset();
        Assert.False(sessionClock.IsRunning);
        Assert.Equal(TimeSpan.Zero, sessionClock.Elapsed);
        Assert.Equal(TimeSpan.Zero, sessionClock.CurrentPartElapsed);
    }
}
