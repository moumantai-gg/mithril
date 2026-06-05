using System.Globalization;
using System.Text.Json;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibration.Common;

namespace Mithril.Tools.MapCalibrationFromScreenshot;

/// <summary>
/// End-to-end pipeline test that needs no PG install, no <c>classdata.tpk</c>,
/// no real screenshot. Synthesises everything from a chosen ground-truth
/// calibration, runs the calibrator, and asserts the recovered transform is
/// close to truth.
///
/// <para>Proves the four image-processing phases (locate, detect, assign,
/// solve) wire together correctly — independent of whatever the real
/// in-game screenshot extraction surface ends up looking like, which a cold
/// session can't run without the game installed. If the self-test passes, the
/// math is right and only icon/screenshot mismatches can cause real-data
/// failures.</para>
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "mithril-852", "self-test");
        if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        Directory.CreateDirectory(workDir);
        Console.WriteLine($"[self-test] workdir: {workDir}");

        // ---------------------------------------------------------------------
        // 1. Synthesise an "area map" texture: noisy but high-contrast so NCC
        //    has something to lock onto. Embeds a few low-frequency gradients +
        //    sharp dots so the locator + the icon-matcher both have signal.
        // ---------------------------------------------------------------------
        const int textureW = 800;
        const int textureH = 600;
        var texturePixels = MakeSyntheticTexture(textureW, textureH, seed: 1234);
        var texturePath = Path.Combine(workDir, "AreaSelfTest.png");
        ImageIo.SaveGrayPng(new GrayImage(textureW, textureH, texturePixels), texturePath);

        // ---------------------------------------------------------------------
        // 2. Synthesise icon templates for each landmark Type. Teardrop shape so
        //    pivot=(0.5, 0) matters — a centre-anchor solve would visibly miss.
        // ---------------------------------------------------------------------
        var iconsDir = Path.Combine(workDir, "icons");
        Directory.CreateDirectory(iconsDir);

        // Distinct geometry per type so NCC doesn't cross-match (real PG icons
        // have different artwork per type; in the synthetic harness we get the
        // same effect by varying width/height — NCC against a different-sized
        // template can't reach the same anchor positions and scores poorly on
        // the wrong shape).
        var iconSpecs = new[]
        {
            new IconSpec("landmark_portal", "Portal", Width: 24, Height: 32, LuminanceRgb: 60),
            new IconSpec("landmark_telepad", "TeleportationPlatform", Width: 28, Height: 22, LuminanceRgb: 180),
            new IconSpec("landmark_medipillar", "MeditationPillar", Width: 18, Height: 40, LuminanceRgb: 110),
            new IconSpec("LocalPlayerPin_Round_Light_Up", "Player", Width: 20, Height: 28, LuminanceRgb: 220),
        };

        foreach (var spec in iconSpecs)
        {
            var iconPng = Path.Combine(iconsDir, spec.Name + ".png");
            WriteTeardropIconPng(iconPng, spec.Width, spec.Height, spec.LuminanceRgb);
        }
        WriteIconIndex(Path.Combine(iconsDir, "index.json"), iconSpecs);

        // ---------------------------------------------------------------------
        // 3. Define a ground-truth calibration that maps world coords (X, Z) to
        //    texture pixels. Pick non-trivial scale + rotation + origin so a
        //    bug in any component would visibly fail.
        // ---------------------------------------------------------------------
        var truth = new AreaCalibration(
            Scale: 1.2,
            RotationRadians: 0.35,
            OriginX: 400.0,
            OriginY: 300.0,
            ReferenceCount: 0,
            ResidualPixels: 0.0)
        { MirrorNorth = false, CalibrationZoom = 1.0 };

        // ---------------------------------------------------------------------
        // 4. Place landmarks at chosen world coords; project to texture pixels
        //    using truth; write a fake landmarks.json so LandmarksReader works.
        // ---------------------------------------------------------------------
        var landmarks = new (string Type, string Name, WorldCoord World)[]
        {
            ("Portal", "Portal_North", new WorldCoord(-50.0, 0.0, 80.0)),
            ("Portal", "Portal_South", new WorldCoord(75.0, 0.0, -40.0)),
            ("TeleportationPlatform", "Telepad_East", new WorldCoord(100.0, 0.0, 20.0)),
            ("MeditationPillar", "Pillar_Centre", new WorldCoord(0.0, 0.0, -10.0)),
        };
        var landmarksJson = Path.Combine(workDir, "landmarks.json");
        WriteSyntheticLandmarksJson(landmarksJson, "AreaSelfTest", landmarks);

        // ---------------------------------------------------------------------
        // 5. Composite the icons onto the texture at the truth pixel positions.
        //    Use pivot (0.5, 0) — anchor at the bottom tip — so the icon's
        //    bottom-centre lands exactly on the projected texture pixel.
        // ---------------------------------------------------------------------
        var compositePixels = (byte[])texturePixels.Clone();
        // #1076 Phase 7.5: the self-test's synthetic ground truth lives in
        // texture-pixel space (icons are blitted onto the base-texture image);
        // project through a texture-frame view of truth.
        var truthTex = new WorldToTextureCalibration(
            truth.OriginX, truth.OriginY, truth.Scale, truth.RotationRadians,
            truth.MirrorNorth, truth.CalibrationZoom);
        foreach (var (type, _, world) in landmarks)
        {
            var spec = iconSpecs.First(s => s.LandmarkType == type);
            var tex = truthTex.ToTexture(world);
            BlitTeardrop(compositePixels, textureW, textureH,
                anchorX: tex.X, anchorY: tex.Y, width: spec.Width, height: spec.Height, luminance: spec.LuminanceRgb);
        }
        // Player pin at world origin (the player). We pick a coord and use the
        // player-pin variant. Add player landmark separately.
        var playerWorld = new WorldCoord(20.0, 0.0, 15.0);
        var playerTex = truthTex.ToTexture(playerWorld);
        var playerSpec = iconSpecs.First(s => s.LandmarkType == "Player");
        BlitTeardrop(compositePixels, textureW, textureH,
            anchorX: playerTex.X, anchorY: playerTex.Y, width: playerSpec.Width, height: playerSpec.Height,
            luminance: playerSpec.LuminanceRgb);

        // ---------------------------------------------------------------------
        // 6. Build a "screenshot" by padding the composited texture with a
        //    constant border (simulates UI chrome around the in-game map view).
        //    The self-test feeds the known padding back in as --map-rect (the
        //    NCC-ladder auto-detect was retired in the PR-4 cutover).
        // ---------------------------------------------------------------------
        const int padLeft = 50;
        const int padTop = 80;
        const int padRight = 30;
        const int padBottom = 100;
        int screenshotW = textureW + padLeft + padRight;
        int screenshotH = textureH + padTop + padBottom;
        var screenshotPixels = new byte[screenshotW * screenshotH];
        Array.Fill(screenshotPixels, (byte)40); // dark UI chrome
        for (int y = 0; y < textureH; y++)
        {
            Buffer.BlockCopy(compositePixels, y * textureW,
                screenshotPixels, (y + padTop) * screenshotW + padLeft, textureW);
        }
        var screenshotPath = Path.Combine(workDir, "self-test-screenshot.png");
        ImageIo.SaveGrayPng(new GrayImage(screenshotW, screenshotH, screenshotPixels), screenshotPath);

        // ---------------------------------------------------------------------
        // 7. Run the calibrator and check the recovered transform.
        // ---------------------------------------------------------------------
        // No NPCs in the synthetic test — write an empty npcs.json so the
        // calibrator's NpcsReader.LoadForArea returns an empty list.
        var npcsJson = Path.Combine(workDir, "npcs.json");
        File.WriteAllText(npcsJson, "{}");

        var inputs = new CalibrationInputs(
            ScreenshotPath: screenshotPath,
            AreaMapPath: texturePath,
            IconsDir: iconsDir,
            LandmarksJsonPath: landmarksJson,
            NpcsJsonPath: npcsJson,
            Area: "AreaSelfTest",
            Zoom: 1.0,
            PlayerCoord: (playerWorld.X, playerWorld.Z),
            // Known padding from step 6; the NCC auto-detect that used to find
            // this synthetic rect was retired in the PR-4 cutover, so the self
            // test now feeds the padding back as an explicit --map-rect.
            MapRectOverride: (padLeft, padTop, textureW, textureH),
            DetectionThreshold: 0.5,
            IconRenderSizeOverride: 0,
            IconSizeOverrides: new Dictionary<string, (int, int)>(),
            ExcludedLandmarkTypes: new HashSet<string>(),
            DebugImagePath: null,
            ProjectionOverlayPath: null,
            MaskDebugPath: null,
            UseBorderMask: false,
            DetectionsCsvPath: null,
            IgnoreTypes: false,
            Seed: null);
        var result = ScreenshotCalibrator.Calibrate(inputs);

        if (result.Calibration is null)
        {
            Console.Error.WriteLine($"[self-test] FAIL: {result.FailureReason}");
            return 1;
        }

        var cal = result.Calibration;
        Console.WriteLine();
        Console.WriteLine($"[self-test] recovered scale={cal.Scale:0.0000} (truth 1.2000), rot={cal.RotationRadians:0.000} rad (truth 0.350), origin=({cal.OriginX:0.0},{cal.OriginY:0.0}) (truth 400,300)");
        Console.WriteLine($"[self-test] residualPixels={cal.ResidualPixels:0.00}, mirrorNorth={cal.MirrorNorth}");

        bool ok = true;
        if (cal.ResidualPixels > 12.0)
        {
            Console.Error.WriteLine($"  FAIL: residualPixels {cal.ResidualPixels:0.00} > 12.0 threshold");
            ok = false;
        }
        if (Math.Abs(cal.Scale - 1.2) > 0.05)
        {
            Console.Error.WriteLine($"  FAIL: scale {cal.Scale:0.0000} differs from truth 1.2 by > 0.05");
            ok = false;
        }
        if (Math.Abs(NormaliseAngle(cal.RotationRadians - 0.35)) > 0.02)
        {
            Console.Error.WriteLine($"  FAIL: rotation {cal.RotationRadians:0.0000} differs from truth 0.35 rad by > 0.02 rad");
            ok = false;
        }
        if (Math.Abs(cal.OriginX - 400.0) > 5.0)
        {
            Console.Error.WriteLine($"  FAIL: originX {cal.OriginX:0.0} differs from truth 400 by > 5");
            ok = false;
        }
        if (Math.Abs(cal.OriginY - 300.0) > 5.0)
        {
            Console.Error.WriteLine($"  FAIL: originY {cal.OriginY:0.0} differs from truth 300 by > 5");
            ok = false;
        }
        if (cal.MirrorNorth)
        {
            Console.Error.WriteLine("  FAIL: solver chose mirrored handedness on a no-mirror truth set");
            ok = false;
        }
        if (cal.ReferenceCount < 4)
        {
            Console.Error.WriteLine($"  FAIL: only {cal.ReferenceCount} references used (expected >= 4: 2 portals + 1 telepad + 1 pillar)");
            ok = false;
        }

        // ---------------------------------------------------------------------
        // 8. Round-trip through BaselineFile to make sure the writer emits JSON
        //    the BundledBaselineLoader (in src/Mithril.MapCalibration/) can read.
        // ---------------------------------------------------------------------
        var baselinePath = Path.Combine(workDir, "map-calibration-baseline.json");
        File.WriteAllText(baselinePath,
            """{ "$schema": "https://moumantai-gg.github.io/mithril/map-calibration-baseline-v1.json", "schemaVersion": 1, "anchors": {} }""");
        BaselineFile.UpsertAnchor(baselinePath, "AreaSelfTest", cal);
        var roundTrip = File.ReadAllText(baselinePath);
        if (!roundTrip.Contains("\"AreaSelfTest\"", StringComparison.Ordinal) ||
            !roundTrip.Contains("\"scale\"", StringComparison.Ordinal) ||
            !roundTrip.Contains("\"residualPixels\"", StringComparison.Ordinal) ||
            !roundTrip.Contains("\"$schema\"", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("  FAIL: round-tripped baseline JSON missing expected fields:");
            Console.Error.WriteLine(roundTrip);
            ok = false;
        }
        else
        {
            Console.WriteLine($"[self-test] baseline round-trip OK ({new FileInfo(baselinePath).Length} bytes)");
        }

        if (!ok)
        {
            return 1;
        }
        Console.WriteLine("[self-test] PASS");
        return 0;
    }

    private record IconSpec(string Name, string LandmarkType, int Width, int Height, int LuminanceRgb);

    private static byte[] MakeSyntheticTexture(int width, int height, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[width * height];
        // Low-frequency gradient + per-pixel noise. Gradient gives the locator
        // strong global signal; noise gives the locator strong local signal.
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double gradient = 80 + 80.0 * x / width + 60.0 * y / height;
                int noise = rng.Next(-30, 31);
                int v = (int)gradient + noise;
                if (v < 0) v = 0; else if (v > 255) v = 255;
                data[y * width + x] = (byte)v;
            }
        }
        return data;
    }

    // Teardrop classification at sub-pixel position (x, y) within a (width, height) icon.
    // Returns: 0 = outside, 1 = outline (1 px ring), 2 = fill interior.
    private static int TeardropPixel(int x, int y, int width, int height)
    {
        double cx = (width - 1) / 2.0;
        double radius = width / 2.5;
        double circleCy = radius + 1;
        bool inShape, inInterior;
        if (y <= circleCy + 1)
        {
            double dx = x - cx;
            double dy = y - circleCy;
            double r2 = dx * dx + dy * dy;
            inShape = r2 <= radius * radius;
            inInterior = r2 <= (radius - 1.2) * (radius - 1.2);
        }
        else
        {
            double t = (y - circleCy) / (height - 1 - circleCy);
            double halfW = radius * (1.0 - t);
            inShape = Math.Abs(x - cx) <= halfW;
            inInterior = Math.Abs(x - cx) <= halfW - 1.0;
        }
        return inShape ? (inInterior ? 2 : 1) : 0;
    }

    private static void WriteTeardropIconPng(string path, int width, int height, int luminance)
    {
        // Two-tone teardrop: fill interior + darker 1-px outline. Constant-
        // luminance interiors give NCC zero variance (template undefined) — the
        // outline provides the spatial signal NCC needs to actually score.
        int outlineLum = Math.Max(0, luminance - 60);
        var bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var kind = TeardropPixel(x, y, width, height);
                int idx = (y * width + x) * 4;
                if (kind == 0)
                {
                    bgra[idx + 3] = 0;
                }
                else
                {
                    var lum = kind == 1 ? outlineLum : luminance;
                    bgra[idx + 0] = (byte)lum;
                    bgra[idx + 1] = (byte)lum;
                    bgra[idx + 2] = (byte)lum;
                    bgra[idx + 3] = 255;
                }
            }
        }
        ImageIo.SaveBgraPng(bgra, width, height, path);
    }

    private static void BlitTeardrop(byte[] dest, int destW, int destH,
        double anchorX, double anchorY, int width, int height, int luminance)
    {
        // Anchor is the bottom tip (pivot (0.5, 0) in Unity convention). Top-left
        // of the icon rect lands at (anchorX - width/2, anchorY - height + 1).
        int topLeftX = (int)Math.Round(anchorX - width / 2.0);
        int topLeftY = (int)Math.Round(anchorY - (height - 1));
        int outlineLum = Math.Max(0, luminance - 60);
        for (int y = 0; y < height; y++)
        {
            int dy = topLeftY + y;
            if (dy < 0 || dy >= destH) continue;
            for (int x = 0; x < width; x++)
            {
                int dx = topLeftX + x;
                if (dx < 0 || dx >= destW) continue;
                var kind = TeardropPixel(x, y, width, height);
                if (kind == 0) continue;
                dest[dy * destW + dx] = (byte)(kind == 1 ? outlineLum : luminance);
            }
        }
    }

    private static void WriteIconIndex(string path, IconSpec[] specs)
    {
        var icons = specs.Select(s => new IconMeta(
            Name: s.Name,
            File: s.Name + ".png",
            Width: s.Width,
            Height: s.Height,
            PivotX: 0.5,
            PivotY: 0.0,
            LandmarkType: s.LandmarkType)).ToList();
        var doc = new IconIndex(1, icons);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));
    }

    private static void WriteSyntheticLandmarksJson(string path, string area, (string Type, string Name, WorldCoord World)[] landmarks)
    {
        // Mirror the shape of the real landmarks.json: { "AreaXxx": [ {Type, Name, Loc}, ... ] }
        using var fs = File.Create(path);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WritePropertyName(area);
        w.WriteStartArray();
        foreach (var l in landmarks)
        {
            w.WriteStartObject();
            w.WriteString("Type", l.Type);
            w.WriteString("Name", l.Name);
            w.WriteString("Loc",
                string.Create(CultureInfo.InvariantCulture, $"x:{l.World.X} y:{l.World.Y} z:{l.World.Z}"));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static double NormaliseAngle(double radians)
    {
        var twoPi = 2 * Math.PI;
        var r = radians % twoPi;
        if (r > Math.PI) r -= twoPi;
        if (r < -Math.PI) r += twoPi;
        return r;
    }
}
