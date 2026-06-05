using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Mithril.Reference.Models.Misc;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// mithril#974 cross-component seam test — the test that would have caught the
/// bug. It joins the PRODUCTION reference vocabulary (built via
/// <see cref="ReferenceDataAreaReferenceProvider"/>) to the DETECTION vocabulary
/// (detections keyed by the bundled icon-template manifest's <c>landmarkType</c>,
/// i.e. <see cref="CanonicalLandmarkTypes"/>) and runs both through the real
/// <see cref="TypeAwareRansacSolver"/>. The pre-fix provider emitted
/// <c>landmark_*</c> tokens that were Ordinal-disjoint from the detection keys,
/// so the type-constrained pool was empty and the solver returned 0 inliers /
/// null. Post-fix the vocabularies match and the synthetic fixture cold-solves.
/// </summary>
public sealed class ReferenceProviderSolverSeamTests
{
    // #1076 Phase 6.5: ground-truth world (X,Z) -> texture-pixel transform.
    private static readonly WorldToTextureCalibration Truth = new(
        OriginX: 400.0, OriginY: 300.0, Scale: 1.2, RotationRadians: 0.35,
        MirrorNorth: false, CalibrationZoom: 1.0);

    // Texture 800x600 at native scale, screenshot padded by (50, 80): so
    // texture-pixel == screenshot-pixel - origin.
    private static readonly MapRect Rect = new(
        OriginX: 50, OriginY: 80, Width: 800, Height: 600, TextureWidth: 800, TextureHeight: 600);

    // (PG type, name, worldX, worldZ). NPCs are emitted by the provider from
    // npcs.json; the rest from landmarks.json.
    private static readonly (string Type, string Name, double X, double Z)[] Points =
    [
        ("Portal", "Serbule Portal", -50.0, 80.0),
        ("Portal", "South Portal", 75.0, -40.0),
        ("TeleportationPlatform", "Telepad", 100.0, 20.0),
        ("MeditationPillar", "Pillar", 0.0, -10.0),
        ("Npc", "Marna", 40.0, 60.0),
        ("Npc", "Joeh", -30.0, -55.0),
    ];

    [Fact]
    public void Provider_vocabulary_pairs_with_manifest_detection_vocabulary()
    {
        // Build the area reference set through the PRODUCTION provider.
        var fake = new FakeAreaReferenceData()
            .WithLandmarks("AreaSerbule",
                Points.Where(p => p.Type != CanonicalLandmarkTypes.Npc)
                    .Select(p => new Landmark
                    {
                        Name = p.Name,
                        Type = p.Type,
                        Loc = $"x:{p.X.ToString(System.Globalization.CultureInfo.InvariantCulture)} y:0 " +
                              $"z:{p.Z.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    })
                    .ToArray());
        foreach (var npc in Points.Where(p => p.Type == CanonicalLandmarkTypes.Npc))
        {
            fake.WithNpc("AreaSerbule", npc.Name,
                pos: $"x:{npc.X.ToString(System.Globalization.CultureInfo.InvariantCulture)} y:0 " +
                     $"z:{npc.Z.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        var refs = new ReferenceDataAreaReferenceProvider(fake).ForArea(new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule"));
        refs.Should().HaveCount(Points.Length);
        // Provider speaks the canonical vocabulary…
        refs.Select(r => r.Type).Distinct().Should().OnlyContain(t => CanonicalLandmarkTypes.All.Contains(t));

        // …and the detector keys detections by IconTemplate.LandmarkType — the
        // SAME vocabulary. Synthesise detections by projecting each ref's world
        // coord through Truth into screenshot space, keyed by its PG type.
        var detections = new Dictionary<string, List<TypedDetection>>(StringComparer.Ordinal);
        foreach (var p in Points)
        {
            var tex = Truth.ToTexture(new WorldCoord(p.X, 0, p.Z));
            var det = new TypedDetection(p.Type, p.Name, new CroppedFramePixel(tex.X + Rect.OriginX, tex.Y + Rect.OriginY), Score: 0.9);
            if (!detections.TryGetValue(p.Type, out var list)) { list = new(); detections[p.Type] = list; }
            list.Add(det);
        }

        var (cal, inliers) = TypeAwareRansacSolver.Solve(detections, refs.ToList(), Rect);

        cal.Should().NotBeNull("the provider + detection vocabularies must overlap so the type-constrained pool is non-empty (mithril#974)");
        inliers.Should().NotBeEmpty();
        inliers.Count.Should().BeGreaterThanOrEqualTo(4);
        Math.Abs(cal!.Scale - Truth.Scale).Should().BeLessThan(0.05);
    }
}
