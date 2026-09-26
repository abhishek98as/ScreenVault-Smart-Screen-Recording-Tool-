using System.Buffers;

namespace ScreenVault.Core.Infrastructure;

public static class BufferPool
{
    public static readonly ArrayPool<byte> Bytes = ArrayPool<byte>.Shared;
    public static readonly ArrayPool<float> Floats = ArrayPool<float>.Shared;
}
