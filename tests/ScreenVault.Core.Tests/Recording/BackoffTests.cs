using ScreenVault.Core.Recording;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Recording;

public sealed class BackoffTests
{
    [Fact]
    public void Backoff_IncreasesDelay_OnFailures()
    {
        var backoff = new Backoff();

        backoff.GetCurrentDelay().ShouldBe(TimeSpan.Zero);

        backoff.RecordFailure();
        backoff.GetCurrentDelay().ShouldBe(TimeSpan.FromMilliseconds(500));

        backoff.RecordFailure();
        backoff.GetCurrentDelay().ShouldBe(TimeSpan.FromSeconds(1));

        backoff.RecordFailure();
        backoff.GetCurrentDelay().ShouldBe(TimeSpan.FromSeconds(2));

        backoff.ConsecutiveFailures.ShouldBe(3);
    }

    [Fact]
    public void Reset_ClearsFailures()
    {
        var backoff = new Backoff();
        backoff.RecordFailure();
        backoff.RecordFailure();

        backoff.Reset();

        backoff.ConsecutiveFailures.ShouldBe(0);
        backoff.GetCurrentDelay().ShouldBe(TimeSpan.Zero);
    }
}
