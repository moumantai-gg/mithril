using System;
using System.Buffers.Binary;
using System.IO;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;

/// <summary>
/// Reads dimensions from a PNG file's IHDR chunk without loading the full image.
/// </summary>
internal static class PngHeader
{
    // PNG file layout:
    //   bytes 0–7:   PNG signature (8 bytes)
    //   bytes 8–11:  IHDR chunk length (4 bytes, always 13)
    //   bytes 12–15: "IHDR" chunk type (4 bytes)
    //   bytes 16–19: width  (uint32 big-endian)
    //   bytes 20–23: height (uint32 big-endian)
    //   ...

    private const int WidthOffset = 16;
    private const int HeaderBytesNeeded = 24; // 8 sig + 4 len + 4 type + 4 W + 4 H

    /// <summary>
    /// Returns the width and height recorded in the PNG IHDR chunk.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Thrown when the file is too short to contain a valid IHDR chunk, does not
    /// have a valid PNG signature, or records out-of-int-range dimensions.
    /// </exception>
    public static (int Width, int Height) ReadDimensions(string path)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[HeaderBytesNeeded];
        int read = 0;
        while (read < HeaderBytesNeeded)
        {
            int n = fs.Read(buf, read, HeaderBytesNeeded - read);
            if (n == 0)
                throw new InvalidDataException(
                    $"PNG file too short to read IHDR dimensions: {path}");
            read += n;
        }

        // Validate PNG signature so a wrong-format or truncated file fails with a
        // clear error instead of returning garbage from bytes 16-23.
        ReadOnlySpan<byte> pngSig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!buf.AsSpan(0, 8).SequenceEqual(pngSig))
            throw new InvalidDataException($"File does not have a valid PNG signature: {path}");

        uint uw = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(WidthOffset, 4));
        uint uh = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(WidthOffset + 4, 4));
        if (uw == 0 || uw > int.MaxValue || uh == 0 || uh > int.MaxValue)
            throw new InvalidDataException(
                $"PNG IHDR contains out-of-range dimensions ({uw}×{uh}): {path}");

        return ((int)uw, (int)uh);
    }
}
