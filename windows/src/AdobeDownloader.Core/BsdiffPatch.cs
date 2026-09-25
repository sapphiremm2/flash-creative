using SharpCompress.Compressors.BZip2;
using CompressionMode = SharpCompress.Compressors.CompressionMode;

namespace AdobeDownloader.Core;

/// <summary>BSDIFF40 decoding using bounded streams; publication and trusted hashes belong to the caller.</summary>
public static class BsdiffPatch
{
    public static void Apply(Stream baseline, byte[] patch, Stream output, long expectedSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!baseline.CanSeek || !baseline.CanRead || !output.CanWrite) throw new ArgumentException("Invalid patch streams.");
        if (patch.Length < 32 || !patch.AsSpan(0, 8).SequenceEqual("BSDIFF40"u8)) throw new InvalidDataException("Unsupported patch format.");
        var controlLength = Number(patch.AsSpan(8, 8)); var diffLength = Number(patch.AsSpan(16, 8));
        var size = Number(patch.AsSpan(24, 8));
        if (controlLength < 0 || diffLength < 0 || size < 0 || size != expectedSize ||
            controlLength > patch.Length - 32 || diffLength > patch.Length - 32 - controlLength)
            throw new InvalidDataException("Invalid patch block lengths or target size.");
        using var controlInput = new MemoryStream(patch, 32, (int)controlLength, false);
        using var diffInput = new MemoryStream(patch, 32 + (int)controlLength, (int)diffLength, false);
        using var extraInput = new MemoryStream(patch, 32 + (int)(controlLength + diffLength), patch.Length - 32 - (int)(controlLength + diffLength), false);
        using var controls = BZip2Stream.Create(controlInput, CompressionMode.Decompress, false);
        using var diffs = BZip2Stream.Create(diffInput, CompressionMode.Decompress, false);
        using var extras = BZip2Stream.Create(extraInput, CompressionMode.Decompress, false);
        var buffer = new byte[64 * 1024]; var old = new byte[buffer.Length]; var tuple = new byte[24];
        long oldPosition = 0, newPosition = 0, diffRead = 0, extraRead = 0; var tuples = 0;
        while (newPosition < size)
        {
            ct.ThrowIfCancellationRequested();
            if (++tuples > 1_000_000) throw new InvalidDataException("Patch control limit exceeded.");
            controls.ReadExactly(tuple);
            var add = Number(tuple.AsSpan(0, 8)); var copy = Number(tuple.AsSpan(8, 8)); var seek = Number(tuple.AsSpan(16, 8));
            if (add < 0 || copy < 0 || add > size - newPosition || copy > size - newPosition - add)
                throw new InvalidDataException("Patch writes beyond the declared target.");
            long offset = 0;
            while (offset < add)
            {
                ct.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, add - offset);
                diffs.ReadExactly(buffer.AsSpan(0, count));
                var position = checked(oldPosition + offset);
                var end = checked(position + count);
                var from = Math.Max(0, position); var to = Math.Min(baseline.Length, end);
                if (from < to)
                {
                    baseline.Position = from;
                    var length = (int)(to - from); baseline.ReadExactly(old.AsSpan(0, length));
                    var start = (int)(from - position);
                    for (var i = 0; i < length; i++) buffer[start + i] = unchecked((byte)(buffer[start + i] + old[i]));
                }
                output.Write(buffer, 0, count); offset += count;
            }
            oldPosition = checked(oldPosition + add); newPosition += add; diffRead += add; extraRead += copy;
            long remaining = copy;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, remaining);
                extras.ReadExactly(buffer.AsSpan(0, count)); output.Write(buffer, 0, count); remaining -= count;
            }
            newPosition += copy; oldPosition = checked(oldPosition + seek);
        }
        if (controls.ReadByte() != -1) throw new InvalidDataException("Unused patch controls.");
        // Adobe pads diff/extra streams with zeros to targetSize + 1. Consume bounded
        // padding to validate the bzip2 footer; nonzero or excessive padding is invalid.
        DrainPadding(diffs, checked(size + 1 - diffRead));
        DrainPadding(extras, checked(size + 1 - extraRead));
        void DrainPadding(Stream stream, long limit)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) return;
                limit -= read;
                if (limit < 0 || buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0) >= 0)
                    throw new InvalidDataException("Invalid patch padding.");
            }
        }
    }

    private static long Number(ReadOnlySpan<byte> bytes)
    {
        long value = bytes[7] & 0x7f;
        for (var i = 6; i >= 0; i--) value = (value << 8) | bytes[i];
        return (bytes[7] & 0x80) == 0 ? value : -value;
    }
}
