using ScreenVault.Core.Audio;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Audio;

public sealed class LevelMeterTests
{
    [Fact]
    public void LevelMeter_CalculatesPeakAndRmsCorrectly()
    {
        var meter = new LevelMeter();

        meter.PeakDb.ShouldBe(LevelMeter.SilenceFloorDb);
        meter.RmsDb.ShouldBe(LevelMeter.SilenceFloorDb);
        meter.HeldPeakDb.ShouldBe(LevelMeter.SilenceFloorDb);

        // Feed full scale 1.0 audio
        var data = new float[200];
        Array.Fill(data, 1.0f);
        meter.Update(data, 100);

        meter.PeakDb.ShouldBe(0f);
        meter.HeldPeakDb.ShouldBe(0f);
        meter.RmsDb.ShouldBeInRange(-1f, 0f);
    }

    [Fact]
    public void LevelMeter_PeakHold_MaintainsPeakDuringSilence()
    {
        var meter = new LevelMeter();

        var fullScale = new float[200];
        Array.Fill(fullScale, 1.0f);
        meter.Update(fullScale, 100);

        meter.HeldPeakDb.ShouldBe(0f);

        // Feed silence immediately
        var silence = new float[200];
        meter.Update(silence, 100);

        // Peak hold should remain at 0 dB during the 1.5s window
        meter.HeldPeakDb.ShouldBe(0f);
    }

    [Fact]
    public void LevelMeter_DecaysTowardsSilenceFloor_AndClampsAtMinus60Db()
    {
        var meter = new LevelMeter();

        // Feed quiet signal
        var quiet = new float[200];
        Array.Fill(quiet, 0.00001f); // Below floor
        meter.Update(quiet, 100);

        meter.PeakDb.ShouldBe(LevelMeter.SilenceFloorDb);
        meter.RmsDb.ShouldBe(LevelMeter.SilenceFloorDb);
    }

    [Fact]
    public void LevelMeter_LinearToDb_HandlesZeroAndExtremes()
    {
        LevelMeter.LinearToDb(0f).ShouldBe(-60f);
        LevelMeter.LinearToDb(-0.5f).ShouldBe(-60f);
        LevelMeter.LinearToDb(1.0f).ShouldBe(0f);
        LevelMeter.LinearToDb(0.5f).ShouldBeInRange(-6.1f, -5.9f);
    }
}
