using ScreenVault.Core.Audio;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Audio;

public sealed class FloatRingBufferTests
{
    [Fact]
    public void WriteAndRead_PreservesDataInOrder()
    {
        var ring = new FloatRingBuffer(100);
        var input = new float[] { 1.0f, 2.0f, 3.0f, 4.0f };

        ring.Write(input).ShouldBe(4);
        ring.AvailableFloats.ShouldBe(4);

        var output = new float[4];
        ring.Read(output).ShouldBe(4);

        output.ShouldBe(input);
        ring.AvailableFloats.ShouldBe(0);
    }

    [Fact]
    public void WrapAround_WorksSeamlessly()
    {
        var ring = new FloatRingBuffer(10);

        // Fill 8, read 8
        var d1 = new float[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        ring.Write(d1);
        var o1 = new float[8];
        ring.Read(o1);

        // Now write 6 (this wraps around the circular array)
        var d2 = new float[] { 10, 20, 30, 40, 50, 60 };
        ring.Write(d2).ShouldBe(6);

        var o2 = new float[6];
        ring.Read(o2).ShouldBe(6);
        o2.ShouldBe(d2);
    }

    [Fact]
    public void Skip_DiscardsSpecifiedNumberOfFloats()
    {
        var ring = new FloatRingBuffer(10);
        ring.Write(new float[] { 1, 2, 3, 4, 5 });

        ring.Skip(2);
        ring.AvailableFloats.ShouldBe(3);

        var output = new float[3];
        ring.Read(output);
        output.ShouldBe(new float[] { 3, 4, 5 });
    }
}
