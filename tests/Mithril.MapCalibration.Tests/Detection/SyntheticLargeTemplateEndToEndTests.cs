using System;
using System.Collections.Generic;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// Always-on CI gate: proves that <see cref="IconRenderScaler"/> is wired into
/// the live detection path and that the detect→solve engine succeeds when
/// templates are large (≥128 px max-dim) but icons are blitted into the screenshot
/// at the small on-screen render size (32 px), so the scaler must downscale the
/// templates to match before NCC.
///
/// <para>The baseline <see cref="SyntheticEndToEndTests"/> authors templates
/// already at render size (18–40 px), all ≤ <see cref="IconRenderScaler.ScaleSearchThresholdPx"/>
/// = 64 px, so <see cref="IconRenderScaler.RenderSized"/> is a total no-op on
/// the baseline fixture — render-size bugs (mithril#916 bugs 1 &amp; 2) were invisible
/// to that test. This fixture forces the scaler to activate: templates are authored
/// at ~160–200 px max-dim while icons are blitted at 32 px, so without the
/// downscale step every template is larger than the blob crop and NCC yields zero
/// detections.</para>
///
/// <para>Icons are blitted by bilinearly resizing the large templates to the
/// target render size and compositing the alpha-masked result onto the texture, so
/// the on-screen icons are <em>exactly</em> what the scaler will produce from the
/// templates — guaranteeing high NCC scores even for slightly soft edges. This
/// strategy isolates the test from teardrop-rendering artefacts while still
/// exercising the full detect→scale→match→solve pipeline.</para>
///
/// <para><see cref="DetectionRequest.RenderSizePx"/> is pinned to 32 to exercise
/// bug-fix 1 (<see cref="DeviationBlobCalibrationDetector"/> wiring the scaler)
/// and bug-fix 2 (the pinned-size branch in <see cref="IconRenderScaler.RenderSized"/>
/// rather than the aggregate-NCC sweep).  This fixture is deterministic (fixed
/// RNG seed 4321, pinned RenderSizePx = 32), so both reverts turn it RED
/// reproducibly: reverting fix 1 leaves every large template wider/taller than
/// the blob crop, so no blob is typed; reverting fix 2 makes the aggregate-NCC
/// sweep select a render size other than 32 px on this fixture, so the templates
/// are downscaled to the wrong size and no blob is typed.  Either way the engine
/// returns a null calibration (documented in the reproduction matrix in the PR
/// description).</para>
/// </summary>
public sealed class SyntheticLargeTemplateEndToEndTests
{
    private const int TexW = 800, TexH = 600;

    // Scale factor applied to every icon template to push max-dim well above
    // ScaleSearchThresholdPx (64 px).  A factor of 5 on the widest icon (28 px →
    // 140 px) is sufficient; all templates will be ≥ 90 px on the max-dim axis.
    private const int ScaleFactor = 5;

    // On-screen render size to pin in DetectionRequest.RenderSizePx.
    // 32 px gives reliable blob detection on the synthetic gradient texture while
    // remaining well below the large template max-dims (90–200 px), so the
    // IconRenderScaler must downscale templates to match.
    private const int RenderSizePx = 32;

    // #1076 Phase 6.5: ground truth in texture-pixel frame.
    private static readonly WorldToTextureCalibration Truth = new(
        OriginX: 380.0, OriginY: 280.0, Scale: 1.1, RotationRadians: 0.2,
        MirrorNorth: false, CalibrationZoom: 1.0);

    // Large templates: same shapes as SyntheticMap.DefaultIcons, scaled up.
    // All max-dims are ≥ 90 px, well above ScaleSearchThresholdPx = 64 px.
    private static readonly SyntheticMap.IconSpec[] LargeIconSpecs =
    [
        new("landmark_portal",     "Portal",                  24 * ScaleFactor, 32 * ScaleFactor, 60),
        new("landmark_telepad",    "TeleportationPlatform",   28 * ScaleFactor, 22 * ScaleFactor, 180),
        new("landmark_medipillar", "MeditationPillar",        18 * ScaleFactor, 40 * ScaleFactor, 110),
        new("landmark_npc",        "Npc",                     20 * ScaleFactor, 28 * ScaleFactor, 220),
    ];

    // Landmark world positions + types.
    private static readonly (string Type, string Icon, double X, double Z)[] LandmarkPositions =
    [
        ("Portal",               "landmark_portal",     -50.0,  80.0),
        ("Portal",               "landmark_portal",      75.0, -40.0),
        ("TeleportationPlatform","landmark_telepad",    100.0,  20.0),
        ("MeditationPillar",     "landmark_medipillar",   0.0, -10.0),
        ("Npc",                  "landmark_npc",          60.0,  55.0),
    ];

    [Fact]
    public void Engine_recovers_truth_when_templates_are_large_and_scaler_downscales_to_render_size()
    {
        // --- Build the fixture -----------------------------------------------
        // Build large templates (all max-dim >> ScaleSearchThresholdPx = 64 px).
        var largeTemplates = SyntheticMap.BuildTemplates(LargeIconSpecs);

        // Sanity: every template must be above the threshold.
        foreach (var t in largeTemplates.Templates)
        {
            Math.Max(t.Gray.Width, t.Gray.Height).Should().BeGreaterThan(
                IconRenderScaler.ScaleSearchThresholdPx,
                $"template '{t.Name}' must be large enough to trigger the scaler");
        }

        // Pre-scale each template to the target render size — this is EXACTLY
        // what IconRenderScaler will produce inside the detector.  We blit these
        // pre-scaled images into the screenshot so the NCC comparison is
        // template-vs-template and achieves near-perfect correlation scores,
        // making the test robust to bilinear-resize artefacts.
        var scaledForBlit = new List<(string Type, string Icon, IconTemplate SmallT)>();
        foreach (var t in largeTemplates.Templates)
        {
            var (rw, rh) = IconRenderScaler.ScaledDims(t.Gray.Width, t.Gray.Height, RenderSizePx);
            var smallGray  = ImageOps.Resize(t.Gray,  rw, rh);
            var smallAlpha = ImageOps.Resize(t.Alpha, rw, rh);
            scaledForBlit.Add((t.LandmarkType, t.Name, t with { Gray = smallGray, Alpha = smallAlpha }));
        }

        // Blit icons into the screenshot.  For each landmark we blit the
        // corresponding small (render-size) template at the projected pixel.
        var texPixels = SyntheticMap.MakeTexture(TexW, TexH, seed: 4321);
        var shotPixels = (byte[])texPixels.Clone();
        var refs = new List<LandmarkReference>();

        foreach (var l in LandmarkPositions)
        {
            // Find the matching small template (first by type — portal appears twice).
            var match = scaledForBlit.Find(s => s.Type == l.Type && s.Icon == l.Icon);
            match.SmallT.Should().NotBeNull($"no template found for type {l.Type}");

            var t = match.SmallT;
            var pix = Truth.ToTexture(new WorldCoord(l.X, 0, l.Z));
            // Blit: small template alpha-composited into shotPixels at anchor (bottom-centre).
            int topLeftX = (int)Math.Round(pix.X - t.Gray.Width / 2.0);
            int topLeftY = (int)Math.Round(pix.Y - (t.Gray.Height - 1));
            for (int y = 0; y < t.Gray.Height; y++)
            {
                int dy = topLeftY + y;
                if (dy < 0 || dy >= TexH) continue;
                for (int x = 0; x < t.Gray.Width; x++)
                {
                    int dx = topLeftX + x;
                    if (dx < 0 || dx >= TexW) continue;
                    byte a = t.Alpha.Pixels[y * t.Gray.Width + x];
                    if (a == 0) continue;
                    shotPixels[dy * TexW + dx] = t.Gray.Pixels[y * t.Gray.Width + x];
                }
            }
            refs.Add(new LandmarkReference(l.Type, l.Icon, new WorldCoord(l.X, 0, l.Z)));
        }

        var shot = new GrayImage(TexW, TexH, shotPixels);
        var tex2 = new GrayImage(TexW, TexH, texPixels);
        var rect = new MapRect(0, 0, TexW, TexH, TexW, TexH);

        // Pin RenderSizePx = 32: exercises the IconRenderScaler wiring in
        // DeviationBlobCalibrationDetector (fix 1) and the pinned-size branch
        // in IconRenderScaler.RenderSized (fix 2).
        var request = new DetectionRequest(shot, tex2, rect, largeTemplates, RimMaskMode.DeviationFlood,
            LowNcc: 0.5, TypeFloor: 0.45,
            BlobOptions: new BlobOptions(MinArea: 8, MaxIconArea: 1500, MinSolidity: 0.25, MaxAspect: 3.5, MinPeak: 0.5))
        {
            RenderSizePx = RenderSizePx,
        };

        // --- Run the engine --------------------------------------------------
        var engine = new MapCalibrationSolveEngine(new DeviationBlobCalibrationDetector(), new CalibrationConfidenceGate());
        var result = engine.Solve(request, refs);

        // --- Assert ----------------------------------------------------------
        result.Calibration.Should().NotBeNull(
            "the engine must cold-solve the large-template fixture — " +
            "if this fails the scaler is not wired into the blob detector or the templates were not downscaled");
        result.RejectReason.Should().BeNull();
        var cal = result.Calibration!;
        Math.Abs(cal.Scale - Truth.Scale).Should().BeLessThan(0.08);
        Math.Abs(NormaliseAngle(cal.RotationRadians - Truth.RotationRadians)).Should().BeLessThan(0.04);
        Math.Abs(cal.OriginX - Truth.OriginX).Should().BeLessThan(8.0);
        Math.Abs(cal.OriginY - Truth.OriginY).Should().BeLessThan(8.0);
        cal.MirrorNorth.Should().BeFalse();
        result.InlierCount.Should().BeGreaterThanOrEqualTo(4);
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
