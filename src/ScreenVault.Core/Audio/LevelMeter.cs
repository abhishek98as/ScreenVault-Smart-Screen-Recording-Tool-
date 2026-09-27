namespace ScreenVault.Core.Audio;

public sealed class LevelMeter
{
    public const float SilenceFloorDb = -60f;
    private const float DecayDbPerSec = 20f;
    private const double PeakHoldSeconds = 1.5;

    private int _peakDbBits = BitConverter.SingleToInt32Bits(SilenceFloorDb);
    private int _rmsDbBits = BitConverter.SingleToInt32Bits(SilenceFloorDb);
    private int _heldPeakDbBits = BitConverter.SingleToInt32Bits(SilenceFloorDb);

    private readonly object _stateLock = new();
    private DateTime _lastUpdateUtc = DateTime.UtcNow;
    private DateTime _heldPeakExpiresUtc = DateTime.MinValue;

    // Loopback capture delivers no data at all while nothing is playing; without this the meters
    // would freeze at the last level instead of falling back to silence.
    private const double StaleAfterSeconds = 0.25;
    private long _lastUpdateTicks = DateTime.UtcNow.Ticks;

    public float PeakDb => DecayIfStale(BitConverter.Int32BitsToSingle(Volatile.Read(ref _peakDbBits)));
    public float RmsDb => IsStale(out _) ? SilenceFloorDb : BitConverter.Int32BitsToSingle(Volatile.Read(ref _rmsDbBits));
    public float HeldPeakDb => DecayIfStale(BitConverter.Int32BitsToSingle(Volatile.Read(ref _heldPeakDbBits)));

    private bool IsStale(out double staleSeconds)
    {
        var last = new DateTime(Interlocked.Read(ref _lastUpdateTicks), DateTimeKind.Utc);
        staleSeconds = (DateTime.UtcNow - last).TotalSeconds - StaleAfterSeconds;
        return staleSeconds > 0;
    }

    private float DecayIfStale(float db)
    {
        return IsStale(out var staleSeconds)
            ? Math.Max(SilenceFloorDb, db - (float)(DecayDbPerSec * 3 * staleSeconds))
            : db;
    }

    public void Update(ReadOnlySpan<float> stereoBuffer, int frames)
    {
        var totalFloats = frames * 2;
        if (totalFloats == 0) return;

        var peak = 0f;
        var sumSquares = 0.0;

        for (var i = 0; i < totalFloats; i++)
        {
            var val = stereoBuffer[i];
            var abs = MathF.Abs(val);
            if (abs > peak) peak = abs;
            sumSquares += val * val;
        }

        var rms = (float)Math.Sqrt(sumSquares / totalFloats);

        var blockPeakDb = LinearToDb(peak);
        var blockRmsDb = LinearToDb(rms);

        var now = DateTime.UtcNow;

        lock (_stateLock)
        {
            var dt = (float)(now - _lastUpdateUtc).TotalSeconds;
            _lastUpdateUtc = now;
            if (dt < 0f || dt > 1.0f) dt = 0.02f;

            var currentPeak = BitConverter.Int32BitsToSingle(_peakDbBits);
            var currentHeld = BitConverter.Int32BitsToSingle(_heldPeakDbBits);

            // Fast attack, 20 dB/s decay
            float newPeak;
            if (blockPeakDb >= currentPeak)
            {
                newPeak = blockPeakDb;
            }
            else
            {
                newPeak = Math.Max(SilenceFloorDb, currentPeak - (DecayDbPerSec * dt));
            }

            // Peak hold for 1.5s
            float newHeld;
            if (blockPeakDb >= currentHeld)
            {
                newHeld = blockPeakDb;
                _heldPeakExpiresUtc = now.AddSeconds(PeakHoldSeconds);
            }
            else if (now >= _heldPeakExpiresUtc)
            {
                newHeld = newPeak;
            }
            else
            {
                newHeld = currentHeld;
            }

            Interlocked.Exchange(ref _peakDbBits, BitConverter.SingleToInt32Bits(newPeak));
            Interlocked.Exchange(ref _rmsDbBits, BitConverter.SingleToInt32Bits(blockRmsDb));
            Interlocked.Exchange(ref _heldPeakDbBits, BitConverter.SingleToInt32Bits(newHeld));
            Interlocked.Exchange(ref _lastUpdateTicks, now.Ticks);
        }
    }

    public void Reset()
    {
        lock (_stateLock)
        {
            Interlocked.Exchange(ref _peakDbBits, BitConverter.SingleToInt32Bits(SilenceFloorDb));
            Interlocked.Exchange(ref _rmsDbBits, BitConverter.SingleToInt32Bits(SilenceFloorDb));
            Interlocked.Exchange(ref _heldPeakDbBits, BitConverter.SingleToInt32Bits(SilenceFloorDb));
            _heldPeakExpiresUtc = DateTime.MinValue;
            _lastUpdateUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref _lastUpdateTicks, _lastUpdateUtc.Ticks);
        }
    }

    public static float LinearToDb(float linear)
    {
        if (linear <= 0.001f) return SilenceFloorDb; // -60 dB floor
        var db = 20f * MathF.Log10(linear);
        return Math.Clamp(db, SilenceFloorDb, 0f);
    }
}
