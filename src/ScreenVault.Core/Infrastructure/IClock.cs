namespace ScreenVault.Core.Infrastructure;

public interface IClock
{
    DateTime UtcNow { get; }
    long Timestamp { get; }
    double SecondsBetween(long startTimestamp, long endTimestamp);
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTime UtcNow => DateTime.UtcNow;

    public long Timestamp => System.Diagnostics.Stopwatch.GetTimestamp();

    public double SecondsBetween(long startTimestamp, long endTimestamp)
    {
        return (endTimestamp - startTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
    }
}
