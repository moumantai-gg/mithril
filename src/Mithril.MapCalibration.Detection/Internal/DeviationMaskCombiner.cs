namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// OR-combines the floor-boundary mask (texture-side) and fog-of-war mask
/// (screenshot-side) into a single binary mask consumed by the deviation step
/// (mithril#1116). Pixels masked in EITHER source are masked in the output.
/// Either input may be null (= no contribution from that source); output is
/// always non-null at the requested dimensions.
/// </summary>
internal static class DeviationMaskCombiner
{
    public static GrayImage Combine(GrayImage? floor, GrayImage? fog, int width, int height)
    {
        int n = width * height;
        var combined = new byte[n];

        if (floor is null && fog is null)
            return new GrayImage(width, height, combined);

        // Validate inputs match requested dimensions; if they don't, treat as
        // null for that source (defensive — caller bug shouldn't crash detector).
        bool floorOk = floor is not null && floor.Width == width && floor.Height == height;
        bool fogOk = fog is not null && fog.Width == width && fog.Height == height;

        for (int i = 0; i < n; i++)
        {
            byte a = floorOk ? floor!.Pixels[i] : (byte)0;
            byte b = fogOk ? fog!.Pixels[i] : (byte)0;
            combined[i] = (a > 0 || b > 0) ? (byte)255 : (byte)0;
        }
        return new GrayImage(width, height, combined);
    }
}
