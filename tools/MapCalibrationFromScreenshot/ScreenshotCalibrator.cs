using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibration.Common;

namespace Mithril.Tools.MapCalibrationFromScreenshot;

/// <summary>
/// Coordinates the four image-processing phases that turn a screenshot into a
/// solved <see cref="AreaCalibration"/>:
/// <list type="number">
///   <item>locate the map rect inside the screenshot,</item>
///   <item>detect each landmark icon template via NCC,</item>
///   <item>assign detections to <c>landmarks.json</c> entries with a Type filter +
///         brute-force-permutation residual minimisation (cheap RANSAC for the
///         small fan-out the issue cares about),</item>
///   <item>feed the (world, texture-pixel) pairs into
///         <see cref="LandmarkCalibrationSolver.Solve"/>.</item>
/// </list>
///
/// <para>Pivot correction is applied per detection: PG's pin-shaped icons are
/// authored with pivot ≈ (0.5, 0) so the world-anchor pixel is the bottom tip,
/// not the icon centre. Without this the residual systematically misses the
/// 12 px threshold (issue #852 comment).</para>
/// </summary>
internal static class ScreenshotCalibrator
{
    // Default NCC threshold for accepting a detection. NCC peaks at 1.0.
    // 0.5 is a reasonable default — clean PG icon matches typically score
    // 0.5–0.9; the synthetic self-test relies on this floor to suppress
    // cross-shape false positives. Real screenshots may need tuning: PG's
    // rendering effects (shading, color tint, anti-aliasing) push some valid
    // matches into the 0.3–0.45 band. Override via --detection-threshold
    // when a real area has low recall, then verify the assignment is sane
    // before committing the result.

    public static CalibrationResult Calibrate(CalibrationInputs inputs)
    {
        var screenshotGray = ImageIo.LoadGray(inputs.ScreenshotPath);
        var textureGray = ImageIo.LoadGray(inputs.AreaMapPath);
        Console.WriteLine($"[screenshot] {screenshotGray.Width}x{screenshotGray.Height} / texture {textureGray.Width}x{textureGray.Height}");

        byte[]? debugBgra = null;
        int debugW = 0, debugH = 0;
        if (inputs.DebugImagePath is not null)
        {
            (debugBgra, debugW, debugH) = ImageIo.LoadBgra(inputs.ScreenshotPath);
        }

        var iconIndex = IconTemplateExtractor.Load(inputs.IconsDir);
        var landmarks = LandmarksReader.LoadForArea(inputs.LandmarksJsonPath, inputs.Area);
        var npcs = NpcsReader.LoadForArea(inputs.NpcsJsonPath, inputs.Area);
        var allRefs = landmarks.Concat(npcs).ToList();
        Console.WriteLine($"[refs] {landmarks.Count} landmarks + {npcs.Count} NPCs for {inputs.Area} from {Path.GetFileName(inputs.LandmarksJsonPath)} / {Path.GetFileName(inputs.NpcsJsonPath)}");

        // Phase: locate the map rect inside the screenshot.
        // The NCC-ladder `MapRectLocator.AutoDetect` fallback was deleted in the
        // PR-4 cutover to the feature-matching production locator — the in-game
        // refiner is now ORB+RANSAC, and the README has long recommended the
        // `--map-rect` override anyway (sub-30s eyeball in any image viewer).
        MapRect mapRect;
        if (inputs.MapRectOverride is { } o)
        {
            mapRect = new MapRect(o.X, o.Y, o.W, o.H, textureGray.Width, textureGray.Height);
            Console.WriteLine($"[locate] using --map-rect override: ({mapRect.OriginX},{mapRect.OriginY}) size {mapRect.Width}x{mapRect.Height}");
        }
        else
        {
            return new CalibrationResult(
                Calibration: null,
                AssignedReferences: [],
                FailureReason: "--map-rect <x,y,w,h> is now required (the NCC-ladder auto-detect was retired with the feature-matching cutover). Read the visible map area's bbox off the screenshot in any image viewer.");
        }

        // Restrict NCC search to the visible map area. Without this the search
        // covers the entire screenshot including UI chrome, producing
        // guaranteed-noise detections that flood the RANSAC pool and crowd
        // out real matches. Detection coords come out in cropped-image space;
        // translate back to screenshot space by adding mapRect.Origin* before
        // anything else looks at them.
        var mapCrop = ImageOps.Crop(screenshotGray, mapRect.OriginX, mapRect.OriginY, mapRect.Width, mapRect.Height);
        Console.WriteLine($"[detect] cropped screenshot to map area {mapCrop.Width}x{mapCrop.Height} for NCC");

        // Phase: build the detection pool — either from an external typed-
        // detections CSV (the deviation-probe blob-typed front-end, already in
        // full-screenshot coords) or by running whole-image template NCC here.
        Dictionary<string, List<TypedDetection>> detectionsByType;
        List<(IconMeta Icon, Detection Det, int RenderW, int RenderH)> rawDetections;
        if (inputs.DetectionsCsvPath is not null)
        {
            detectionsByType = LoadDetectionsCsv(inputs.DetectionsCsvPath);
            rawDetections = [];
            Console.WriteLine($"[detections-csv] loaded {detectionsByType.Sum(kv => kv.Value.Count)} typed detections from {Path.GetFileName(inputs.DetectionsCsvPath)} (already in screenshot space)");
        }
        else
        {
            (detectionsByType, rawDetections) = DetectIconsByType(mapCrop, inputs.IconsDir, iconIndex, inputs.DetectionThreshold, inputs.IconRenderSizeOverride, inputs.IconSizeOverrides);

            // Translate every detection from crop-space to screenshot-space so
            // downstream (ScreenshotToTexture, debug image, projection overlay)
            // sees consistent coordinates.
            foreach (var typeDets in detectionsByType.Values)
            {
                for (int i = 0; i < typeDets.Count; i++)
                {
                    var d = typeDets[i];
                    typeDets[i] = d with
                    {
                        AnchorScreenshotX = d.AnchorScreenshotX + mapRect.OriginX,
                        AnchorScreenshotY = d.AnchorScreenshotY + mapRect.OriginY,
                    };
                }
            }
            rawDetections = rawDetections
                .Select(t => (t.Icon, t.Det with { X = t.Det.X + mapRect.OriginX, Y = t.Det.Y + mapRect.OriginY, SubX = t.Det.SubX + mapRect.OriginX, SubY = t.Det.SubY + mapRect.OriginY }, t.RenderW, t.RenderH))
                .ToList();
        }

        // Drop detections sitting in the rocky border of an irregular-bordered
        // map. The stone rim matches pin templates at noise level and, with a
        // rectangular map-rect that can't exclude it, floods RANSAC with false
        // positives that out-vote the sparse interior landmarks (observed:
        // Eltibule / KurMountains collapse to a tiny-scale wrong fit). The mask
        // is the edge-connected non-vegetation/water region; interior anchors
        // survive. Opt-in (--border-mask) since flat tan/desert areas have no
        // such border and shouldn't pay for the classification.
        // The mask is needed if we're masking OR just visualizing it (--debug /
        // --mask-debug renders the rim even when masking is off, so you can see
        // what masking WOULD drop before committing to --border-mask).
        if (inputs.UseBorderMask || inputs.MaskDebugPath is not null)
        {
            var (maskBgra, maskW, maskH) = ImageIo.LoadBgra(inputs.ScreenshotPath);
            var border = BorderMask.Compute(maskBgra, maskW, maskH);
            bool InBorder(double ax, double ay)
            {
                var x = (int)Math.Round(ax);
                var y = (int)Math.Round(ay);
                return x < 0 || x >= maskW || y < 0 || y >= maskH || border[y * maskW + x];
            }

            // Visualize BEFORE dropping so the diagnostic shows kept-vs-dropped.
            if (inputs.MaskDebugPath is not null)
            {
                RenderMaskDebug(maskBgra, maskW, maskH, border, detectionsByType,
                    inputs.UseBorderMask, inputs.MaskDebugPath);
            }

            if (inputs.UseBorderMask)
            {
                var dropped = 0;
                foreach (var typeDets in detectionsByType.Values)
                {
                    dropped += typeDets.RemoveAll(d => InBorder(d.AnchorScreenshotX, d.AnchorScreenshotY));
                }
                rawDetections.RemoveAll(t =>
                {
                    var (cx, cy) = t.Det.Centre(t.RenderW, t.RenderH);
                    return InBorder(cx + t.RenderW * (t.Icon.PivotX - 0.5), cy + t.RenderH * (0.5 - t.Icon.PivotY));
                });
                Console.WriteLine($"[border-mask] dropped {dropped} detections sitting in the rocky border");
            }
        }

        // Drop excluded landmark types from the pool BEFORE RANSAC sees them.
        // Used when a template doesn't match PG's actual sprite — keeping its
        // noisy matches in the pool misleads RANSAC into wrong-but-self-
        // consistent calibrations (verified for Npc on Serbule v1).
        foreach (var excluded in inputs.ExcludedLandmarkTypes)
        {
            if (detectionsByType.Remove(excluded))
            {
                Console.WriteLine($"[exclude] dropped landmark type '{excluded}' from RANSAC pool");
            }
        }

        if (debugBgra is not null)
        {
            // Mark every NCC detection that cleared threshold. Cyan rect = match
            // bbox (at the multi-scale render size), red cross = pivot-corrected
            // anchor (the pixel fed to solver).
            foreach (var (icon, det, rw, rh) in rawDetections)
            {
                ImageIo.DrawRect(debugBgra, debugW, debugH, det.X, det.Y, rw, rh, 0, 255, 255);
                var (cx, cy) = det.Centre(rw, rh);
                int ax = (int)Math.Round(cx + rw * (icon.PivotX - 0.5));
                int ay = (int)Math.Round(cy + rh * (0.5 - icon.PivotY));
                ImageIo.DrawCross(debugBgra, debugW, debugH, ax, ay, 4, 255, 0, 0);
            }
        }

        // Per-type summary (informational; assignment happens via RANSAC below
        // over the full pool).
        foreach (var typeGroup in detectionsByType)
        {
            var areaRefs = allRefs.Where(l => string.Equals(l.Type, typeGroup.Key, StringComparison.Ordinal)).ToList();
            Console.WriteLine($"[detect] {typeGroup.Key}: {typeGroup.Value.Count} detections vs {areaRefs.Count} landmarks in area");
        }

        // Phase: assignment via RANSAC over the full (detection, ref) pool.
        // Sequential pairing per-type produced geometrically-incoherent results
        // (sub-meter NPCs paired by file order, not by actual map position).
        // RANSAC picks 2 random same-type pairs, solves a candidate calibration,
        // counts how many other detections project within threshold of a
        // same-type ref. Best inlier count wins.
        List<AssignedReference> assigned;
        if (inputs.Seed is { } s)
        {
            var seedCal = new AreaCalibration(s.Scale, s.Rot, s.Ox, s.Oy, 0, 0.0) { MirrorNorth = s.Mirror };
            assigned = SeedGuidedAssign(detectionsByType, allRefs, mapRect, seedCal).ToList();
            Console.WriteLine($"[seed-icp] {assigned.Count} references assigned");
        }
        else
        {
            assigned = RansacAssign(detectionsByType, allRefs, mapRect, inputs.IgnoreTypes).ToList();
            Console.WriteLine($"[ransac] {assigned.Count} inlier references kept{(inputs.IgnoreTypes ? " (type constraint OFF)" : "")}");
        }

        if (assigned.Count < 2)
        {
            return new CalibrationResult(
                Calibration: null,
                AssignedReferences: assigned,
                FailureReason: $"RANSAC could not find a calibration with >= 2 geometrically-consistent (detection, ref) pairs. Total detections: {detectionsByType.Sum(kv => kv.Value.Count)}. Inspect --debug-image to see if detections are clustered on actual icons.");
        }

        // Final solve + iterative refinement. Standard LO-RANSAC pattern:
        // after the seed-derived inlier set is solved, drop the worst-residual
        // inlier and re-solve; repeat until residual stops improving or only
        // 3 refs remain. Catches the case where one inlier was paired to a
        // nearby ref's icon (e.g. Selphie ↔ Tadion in Serbule's central
        // cluster — 14 world units apart, RANSAC's per-ref dedup gives one
        // detection to one of the two refs and the other ref has to pair
        // with a different — wrong — detection).
        var (cal, finalAssigned) = IterativeRefine(assigned);
        if (cal is null)
        {
            return new CalibrationResult(null, assigned, "solver returned null on RANSAC inliers (degenerate — all collinear?)");
        }
        cal = cal with { CalibrationZoom = inputs.Zoom, Source = CalibrationSource.BundledBaseline };
        assigned = finalAssigned;

        // Phase: render the projection overlay on a fresh screenshot copy.
        // Projects every landmark + NPC ref through the recovered calibration
        // and marks them on the screenshot. Lets the user see exactly where
        // every world coord ends up — calibration accuracy is then a visual
        // judgement (do markers land on actual icons?) rather than an abstract
        // residual number. Inlier set marked separately so the user can see
        // which refs RANSAC actually used.
        if (inputs.ProjectionOverlayPath is not null)
        {
            var (projBgra, projW, projH) = ImageIo.LoadBgra(inputs.ScreenshotPath);
            double sxPerTx = (double)mapRect.Width / mapRect.TextureWidth;
            double syPerTy = (double)mapRect.Height / mapRect.TextureHeight;
            var inlierLabels = new HashSet<string>(assigned.Select(a => a.Label.Split(':').Last().Split(' ')[0]), StringComparer.Ordinal);
            int onScreen = 0, offScreen = 0;
            foreach (var r in allRefs)
            {
                var pred = cal.WorldToWindow(new WorldCoord(r.World.X, 0, r.World.Z));
                int sx = (int)Math.Round(pred.X * sxPerTx + mapRect.OriginX);
                int sy = (int)Math.Round(pred.Y * syPerTy + mapRect.OriginY);
                if (sx < 0 || sx >= projW || sy < 0 || sy >= projH) { offScreen++; continue; }
                onScreen++;
                bool isInlier = inlierLabels.Contains(r.Name.Split(' ')[0]);
                // Yellow cross for every ref, green outline + label-mark for RANSAC inliers.
                ImageIo.DrawCross(projBgra, projW, projH, sx, sy, 5, 255, 255, 0);
                if (isInlier)
                {
                    ImageIo.DrawRect(projBgra, projW, projH, sx - 8, sy - 8, 16, 16, 0, 255, 0);
                }
            }
            // Map-rect outline.
            ImageIo.DrawRect(projBgra, projW, projH, mapRect.OriginX, mapRect.OriginY,
                mapRect.Width, mapRect.Height, 128, 128, 128);
            ImageIo.SaveBgraPng(projBgra, projW, projH, inputs.ProjectionOverlayPath);
            Console.WriteLine($"[overlay] projection overlay -> {inputs.ProjectionOverlayPath}");
            Console.WriteLine($"[overlay]   yellow cross   = every ref projected through the recovered calibration");
            Console.WriteLine($"[overlay]   green rect     = the RANSAC inliers used in the solve");
            Console.WriteLine($"[overlay]   {onScreen} refs landed inside the screenshot, {offScreen} fell outside");
        }

        // Phase: write debug image (deferred so we can color-code RANSAC inliers).
        if (debugBgra is not null && inputs.DebugImagePath is not null)
        {
            // Overlay green rects + crosses for inliers RANSAC actually kept,
            // on top of the cyan/red raw-detection layer. Converts inlier
            // texture-pixel coords back to screenshot pixels for drawing.
            var scaleX = (double)mapRect.Width / mapRect.TextureWidth;
            var scaleY = (double)mapRect.Height / mapRect.TextureHeight;
            foreach (var a in assigned)
            {
                int sx = (int)Math.Round(a.PixelX * scaleX + mapRect.OriginX);
                int sy = (int)Math.Round(a.PixelY * scaleY + mapRect.OriginY);
                // Slightly oversized green rect to stand out over the cyan raw rect.
                ImageIo.DrawRect(debugBgra, debugW, debugH, sx - 12, sy - 12, 24, 24, 0, 255, 0);
                ImageIo.DrawRect(debugBgra, debugW, debugH, sx - 13, sy - 13, 26, 26, 0, 255, 0);
                ImageIo.DrawCross(debugBgra, debugW, debugH, sx, sy, 6, 0, 255, 0);
            }
            ImageIo.DrawRect(debugBgra, debugW, debugH, mapRect.OriginX, mapRect.OriginY,
                mapRect.Width, mapRect.Height, 0, 255, 0);
            ImageIo.SaveBgraPng(debugBgra, debugW, debugH, inputs.DebugImagePath);
            Console.WriteLine($"[debug] annotated screenshot -> {inputs.DebugImagePath}");
            Console.WriteLine("[debug]   cyan rect / red cross = every NCC detection that cleared threshold");
            Console.WriteLine("[debug]   green rect / green cross = the RANSAC inliers actually used in the solve");
            Console.WriteLine("[debug]   green outline = the map rect");
        }

        return new CalibrationResult(cal, assigned, FailureReason: null);
    }

    private static (AreaCalibration? Cal, List<AssignedReference> Refined) IterativeRefine(
        IReadOnlyList<AssignedReference> initial)
    {
        // Solve, identify worst inlier by per-inlier residual, drop it if its
        // residual is significantly worse than the median, re-solve. Stop when
        // dropping the worst doesn't improve overall residual or we're down
        // to 3 inliers (similarity solver needs >= 2; 3 gives a real residual
        // to evaluate).
        var current = initial.ToList();
        AreaCalibration? bestCal = SolveOver(current);
        if (bestCal is null) return (null, current);

        const int MinInliers = 3;
        // Up to 10 iterations to drop accumulated outliers; usually 1-2.
        for (int iter = 0; iter < 10 && current.Count > MinInliers; iter++)
        {
            var perInlier = current.Select(a =>
            {
                var p = bestCal.WorldToWindow(new Mithril.MapCalibration.WorldCoord(a.WorldX, 0, a.WorldZ));
                var dx = p.X - a.PixelX;
                var dy = p.Y - a.PixelY;
                return (Ref: a, Dist: Math.Sqrt(dx * dx + dy * dy));
            }).ToList();
            perInlier.Sort((x, y) => x.Dist.CompareTo(y.Dist));

            var median = perInlier[perInlier.Count / 2].Dist;
            var worst = perInlier[^1];
            // Drop only if the worst is meaningfully worse than the median —
            // otherwise we're carving into legitimate non-affine ceiling
            // residual.
            if (worst.Dist < Math.Max(median * 2.0, 3.0))
            {
                break;
            }

            var candidate = current.Where(r => !ReferenceEquals(r, worst.Ref)).ToList();
            var candidateCal = SolveOver(candidate);
            if (candidateCal is null) break;
            if (candidateCal.ResidualPixels >= bestCal.ResidualPixels)
            {
                break;
            }
            Console.WriteLine($"[refine] iter {iter + 1}: dropped {worst.Ref.Label} (res {worst.Dist:0.00} px); RMS {bestCal.ResidualPixels:0.00} → {candidateCal.ResidualPixels:0.00}");
            bestCal = candidateCal;
            current = candidate;
        }
        return (bestCal, current);
    }

    private static AreaCalibration? SolveOver(IEnumerable<AssignedReference> refs)
    {
        var input = refs
            .Select(a => new LandmarkCalibrationSolver.Reference(a.WorldX, a.WorldZ, new PixelPoint(a.PixelX, a.PixelY)))
            .ToList();
        return LandmarkCalibrationSolver.Solve(input);
    }

    // Inlier threshold for RANSAC: a detection is an inlier of a candidate
    // calibration if its pivot-corrected pixel is within this many texture
    // pixels of where the calibration projects a same-type ref. Tightened
    // from 50 → 15 after first real-screenshot run (Serbule): the looser
    // threshold let noisy NPC detections in the central-town cluster pair
    // with NPCs they weren't actually positioned at, dragging a 3-perfect-
    // landmark fit into a wrong-but-self-consistent 11-inlier fit. 15 px in
    // texture space is ~7 px on a typical screenshot — tighter than icon
    // size, so a detection must be on a real icon to qualify.
    private const double RansacInlierPx = 15.0;

    // Random-sample iterations. 800 handles ~80% outliers at 95% confidence
    // for a 2-point seed; cheap because each iteration is just a 2-point
    // solver invocation + a linear inlier scan over the pool.
    private const int RansacIterations = 800;

    private static IReadOnlyList<AssignedReference> RansacAssign(
        Dictionary<string, List<TypedDetection>> detectionsByType,
        List<LandmarkRef> allRefs,
        MapRect mapRect,
        bool ignoreTypes = false)
    {
        // Build pool: (texture-pixel detection, candidate refs of same type).
        // Work in texture-pixel space so the inlier predicate is in a stable
        // coord system independent of the screenshot's pan/zoom.
        // --ignore-types widens every detection's candidates to ALL refs — the
        // diagnostic for "do anonymous blob centroids register without the type
        // label?". Inlier predicate stays per-detection-best, so a detection can
        // now (wrongly) latch onto a cross-type ref if it's geometrically closer.
        var pool = new List<(TypedDetection Det, double Tx, double Ty, IReadOnlyList<LandmarkRef> Candidates)>();
        foreach (var kv in detectionsByType)
        {
            var typeRefs = ignoreTypes
                ? allRefs
                : allRefs.Where(r => string.Equals(r.Type, kv.Key, StringComparison.Ordinal)).ToList();
            if (typeRefs.Count == 0) continue;
            foreach (var det in kv.Value)
            {
                var (tx, ty) = mapRect.ScreenshotToTexture(det.AnchorScreenshotX, det.AnchorScreenshotY);
                pool.Add((det, tx, ty, typeRefs));
            }
        }
        if (pool.Count < 2) return [];

        var rng = new Random(852);  // deterministic seed for reproducible runs
        int bestInlierCount = 0;
        double bestResidual = double.PositiveInfinity;
        List<AssignedReference> bestAssigned = [];

        for (int iter = 0; iter < RansacIterations; iter++)
        {
            int i1 = rng.Next(pool.Count);
            int i2 = rng.Next(pool.Count);
            if (i1 == i2) continue;
            var e1 = pool[i1];
            var e2 = pool[i2];
            if (Math.Abs(e1.Tx - e2.Tx) < 5 && Math.Abs(e1.Ty - e2.Ty) < 5) continue;

            var r1 = e1.Candidates[rng.Next(e1.Candidates.Count)];
            var r2 = e2.Candidates[rng.Next(e2.Candidates.Count)];
            if (r1.World.X == r2.World.X && r1.World.Z == r2.World.Z) continue;

            var seed = LandmarkCalibrationSolver.Solve([
                new LandmarkCalibrationSolver.Reference(r1.World.X, r1.World.Z, new PixelPoint(e1.Tx, e1.Ty)),
                new LandmarkCalibrationSolver.Reference(r2.World.X, r2.World.Z, new PixelPoint(e2.Tx, e2.Ty)),
            ]);
            if (seed is null) continue;

            // For each pool entry, project each of its candidate refs through
            // the seed; the closest projection wins. If within RansacInlierPx
            // of the detected pixel, it's a candidate inlier.
            //
            // Two-stage de-dup: (a) per detection, keep the single best ref
            // (already in the inner loop). (b) per ref, keep the single best
            // detection — otherwise multiple noisy detections all "claim" the
            // same real landmark, inflating the inlier count and dragging the
            // final solve's residual upward.
            var perDetCandidates = new List<(int PoolIdx, LandmarkRef Ref, double Dist)>();
            for (int pi = 0; pi < pool.Count; pi++)
            {
                var e = pool[pi];
                LandmarkRef? best = null;
                double bestDist = double.PositiveInfinity;
                foreach (var cand in e.Candidates)
                {
                    var pred = seed.WorldToWindow(cand.World);
                    var dx = pred.X - e.Tx;
                    var dy = pred.Y - e.Ty;
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < bestDist) { bestDist = d; best = cand; }
                }
                if (best is not null && bestDist <= RansacInlierPx)
                {
                    perDetCandidates.Add((pi, best, bestDist));
                }
            }

            // Per ref, keep the detection with the smallest distance.
            var bestPerRef = new Dictionary<(double, double), (int PoolIdx, LandmarkRef Ref, double Dist)>();
            foreach (var cand in perDetCandidates)
            {
                var key = (cand.Ref.World.X, cand.Ref.World.Z);
                if (!bestPerRef.TryGetValue(key, out var existing) || cand.Dist < existing.Dist)
                {
                    bestPerRef[key] = cand;
                }
            }

            var inliers = new List<AssignedReference>(bestPerRef.Count);
            foreach (var cand in bestPerRef.Values)
            {
                var e = pool[cand.PoolIdx];
                inliers.Add(new AssignedReference(
                    Label: $"{e.Det.IconName}:{cand.Ref.Name}",
                    WorldX: cand.Ref.World.X,
                    WorldZ: cand.Ref.World.Z,
                    PixelX: e.Tx,
                    PixelY: e.Ty,
                    MatchScore: e.Det.MatchScore));
            }
            // Reject geometrically-degenerate inlier sets: if every inlier is
            // at the SAME small region of the texture (e.g. an edge-artifact
            // cluster where NCC false-positives at noisy small scales),
            // any pairing through them yields a trivial-but-meaningless fit
            // (tiny scale collapses world coords to a point). Require the
            // bounding box of detected pixels to span at least 100 px in the
            // larger dim — a real area calibration must cover meaningful
            // ground.
            if (inliers.Count < 2) continue;
            double minX = inliers.Min(a => a.PixelX), maxX = inliers.Max(a => a.PixelX);
            double minY = inliers.Min(a => a.PixelY), maxY = inliers.Max(a => a.PixelY);
            if (Math.Max(maxX - minX, maxY - minY) < 100) continue;

            // Score the candidate: prefer more inliers, but tie-break by the
            // refit residual over those inliers. A "wrong" seed can collect
            // inliers within the threshold window by chance — the refit over
            // those mis-paired points yields a high residual, while a
            // "correct" seed with the same inlier count refits to near-zero
            // residual.
            var refitRefs = inliers
                .Select(a => new LandmarkCalibrationSolver.Reference(a.WorldX, a.WorldZ, new PixelPoint(a.PixelX, a.PixelY)))
                .ToList();
            var refit = LandmarkCalibrationSolver.Solve(refitRefs);
            if (refit is null) continue;

            bool wins = inliers.Count > bestInlierCount
                     || (inliers.Count == bestInlierCount && refit.ResidualPixels < bestResidual);
            if (wins)
            {
                bestInlierCount = inliers.Count;
                bestResidual = refit.ResidualPixels;
                bestAssigned = inliers;
            }
        }

        if (bestInlierCount > 0)
        {
            Console.WriteLine($"[ransac] best seed had {bestInlierCount} inliers out of {pool.Count} pool entries");
            foreach (var a in bestAssigned)
            {
                Console.WriteLine($"  {a.Label}  world=({a.WorldX:0.0},{a.WorldZ:0.0}) tex=({a.PixelX:0.0},{a.PixelY:0.0}) score={a.MatchScore:0.000}");
            }
        }
        return bestAssigned;
    }

    /// <summary>
    /// Loads a typed-detections CSV (header
    /// <c>screenshotX,screenshotY,type,iconName,score</c>) into the per-type pool.
    /// Coordinates are full-screenshot pixels (the deviation probe emits anchors in
    /// screenshot space), so no crop-space translation is applied.
    /// </summary>
    private static Dictionary<string, List<TypedDetection>> LoadDetectionsCsv(string path)
    {
        var byType = new Dictionary<string, List<TypedDetection>>(StringComparer.Ordinal);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            var c = line.Split(',');
            if (c.Length < 5 || !double.TryParse(c[0], System.Globalization.NumberStyles.Float, inv, out var sx))
                continue; // header or malformed
            var sy = double.Parse(c[1], inv);
            var type = c[2];
            var iconName = c[3];
            var score = double.Parse(c[4], inv);
            if (!byType.TryGetValue(type, out var list))
            {
                list = [];
                byType[type] = list;
            }
            list.Add(new TypedDetection(iconName, sx, sy, score));
        }
        return byType;
    }

    /// <summary>
    /// Border-mask diagnostic: tints the masked rocky-rim region red over the
    /// screenshot and draws every detection's anchor as a cross — green if it
    /// survives the mask (interior), red if it falls in the border. When
    /// <paramref name="maskActive"/> is false the reds are "would be dropped"
    /// rather than actually dropped (so you can preview the mask before opting
    /// in with --border-mask).
    /// </summary>
    private static void RenderMaskDebug(
        byte[] bgra, int w, int h, bool[] border,
        Dictionary<string, List<TypedDetection>> detectionsByType,
        bool maskActive, string outPath)
    {
        // Half-strength red wash over masked pixels (keep terrain readable).
        for (int p = 0; p < w * h; p++)
        {
            if (!border[p]) continue;
            int i = p * 4;
            bgra[i] = (byte)(bgra[i] / 2);          // B
            bgra[i + 1] = (byte)(bgra[i + 1] / 2);  // G
            bgra[i + 2] = (byte)(128 + bgra[i + 2] / 2);  // R
        }

        int kept = 0, inBorder = 0;
        foreach (var typeDets in detectionsByType.Values)
        {
            foreach (var d in typeDets)
            {
                int x = (int)Math.Round(d.AnchorScreenshotX);
                int y = (int)Math.Round(d.AnchorScreenshotY);
                bool isBorder = x < 0 || x >= w || y < 0 || y >= h || border[y * w + x];
                if (isBorder) { inBorder++; ImageIo.DrawCross(bgra, w, h, x, y, 5, 255, 0, 0); }
                else { kept++; ImageIo.DrawCross(bgra, w, h, x, y, 5, 0, 255, 0); }
            }
        }

        ImageIo.SaveBgraPng(bgra, w, h, outPath);
        var verb = maskActive ? "dropped" : "would drop";
        Console.WriteLine($"[mask-debug] {outPath}");
        Console.WriteLine($"[mask-debug]   red wash = masked rocky rim; green cross = kept ({kept}); red cross = {verb} ({inBorder})");
    }

    // Radius ladder (texture px) for the seed-guided ICP. Starts loose enough to
    // absorb a fragile cold seed's drift far from its few anchors, tightens to
    // ~icon size so only true matches survive once the fit has converged.
    private static readonly double[] SeedIcpRadii = [60, 45, 30, 20, 15, 15, 15];

    /// <summary>
    /// Correspondence by guided ICP: project every ref through the current
    /// calibration, snap it to its nearest same-type detection within a
    /// shrinking radius, re-solve over those pairs, repeat. Seeded with a
    /// known-orientation calibration (a fragile cold solve or a frame-invariant
    /// rotation), it converges to a many-point, well-spread least-squares fit on
    /// sparse areas where one-shot RANSAC correspondence is unstable. Each
    /// detection is claimed by at most one ref (closest wins) so duplicate
    /// false-positive detections can't all latch onto the same landmark.
    /// </summary>
    private static IReadOnlyList<AssignedReference> SeedGuidedAssign(
        Dictionary<string, List<TypedDetection>> detectionsByType,
        List<LandmarkRef> allRefs,
        MapRect mapRect,
        AreaCalibration seed)
    {
        // Texture-space detection pool per type (stable coord system, like RANSAC).
        var poolByType = new Dictionary<string, List<(double Tx, double Ty, double Score, string IconName)>>(StringComparer.Ordinal);
        foreach (var kv in detectionsByType)
        {
            var list = new List<(double, double, double, string)>(kv.Value.Count);
            foreach (var det in kv.Value)
            {
                var (tx, ty) = mapRect.ScreenshotToTexture(det.AnchorScreenshotX, det.AnchorScreenshotY);
                list.Add((tx, ty, det.MatchScore, det.IconName));
            }
            poolByType[kv.Key] = list;
        }

        var cal = seed;
        List<AssignedReference> assigned = [];
        foreach (var radius in SeedIcpRadii)
        {
            // Each ref proposes its nearest same-type detection; resolve
            // detection contention by keeping the closest ref per detection.
            var perDet = new Dictionary<(string Type, int DetIdx),
                (LandmarkRef Ref, double Dist, double Tx, double Ty, double Score, string IconName)>();
            foreach (var r in allRefs)
            {
                if (!poolByType.TryGetValue(r.Type, out var pool) || pool.Count == 0) continue;
                var p = cal.WorldToWindow(new WorldCoord(r.World.X, 0, r.World.Z));
                int bestIdx = -1;
                double bestDist = double.PositiveInfinity;
                for (int i = 0; i < pool.Count; i++)
                {
                    var dx = pool[i].Tx - p.X;
                    var dy = pool[i].Ty - p.Y;
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < bestDist) { bestDist = d; bestIdx = i; }
                }
                if (bestIdx < 0 || bestDist > radius) continue;
                var key = (r.Type, bestIdx);
                if (!perDet.TryGetValue(key, out var ex) || bestDist < ex.Dist)
                {
                    var hit = pool[bestIdx];
                    perDet[key] = (r, bestDist, hit.Tx, hit.Ty, hit.Score, hit.IconName);
                }
            }

            var next = perDet.Values
                .Select(v => new AssignedReference(
                    Label: $"{v.IconName}:{v.Ref.Name}",
                    WorldX: v.Ref.World.X,
                    WorldZ: v.Ref.World.Z,
                    PixelX: v.Tx,
                    PixelY: v.Ty,
                    MatchScore: v.Score))
                .ToList();
            if (next.Count < 2) break;

            var newCal = SolveOver(next);
            if (newCal is null) break;
            cal = newCal;
            assigned = next;
        }

        if (assigned.Count > 0)
        {
            Console.WriteLine($"[seed-icp] converged to {assigned.Count} assignments (final radius {SeedIcpRadii[^1]} px)");
            foreach (var a in assigned.OrderBy(a => a.Label, StringComparer.Ordinal))
            {
                Console.WriteLine($"  {a.Label,-50} world=({a.WorldX,7:0.0},{a.WorldZ,7:0.0}) tex=({a.PixelX,7:0.0},{a.PixelY,7:0.0}) score={a.MatchScore:0.000}");
            }
        }
        return assigned;
    }

    private static (Dictionary<string, List<TypedDetection>> ByType, List<(IconMeta Icon, Detection Det, int RenderW, int RenderH)> Raw)
        DetectIconsByType(GrayImage screenshot, string iconsDir, IconIndex iconIndex, double threshold, int iconRenderSizeOverride, IReadOnlyDictionary<string, (int W, int H)> perIconOverrides)
    {
        // Load every template once, gray + alpha pair.
        var templates = new List<(IconMeta Icon, GrayImage Gray, GrayImage Alpha)>();
        foreach (var icon in iconIndex.Icons)
        {
            var iconPath = Path.Combine(iconsDir, icon.File);
            if (!File.Exists(iconPath))
            {
                Console.WriteLine($"  ! template file missing: {iconPath} (skipping)");
                continue;
            }
            var (gray, alpha) = ImageIo.LoadGrayAndAlpha(iconPath);
            templates.Add((icon, gray, alpha));
        }

        // Pick the global render size: PG renders all map icons at a single
        // consistent on-screen pixel size regardless of source artwork
        // dimensions (verified 2026-05-29 user observation). Treating each
        // icon's best scale independently invites cross-scale inconsistency;
        // a single shared render size gives consistent geometry to the solver.
        //
        // For large templates (artwork at sharedassets0's ~256 px), sweep a
        // ladder of target on-screen sizes and pick the one that maximises
        // aggregate evidence (sum of top scores across all icons). For small
        // templates already at render size (e.g. self-test's 24-32 px synth),
        // skip the sweep and use native — there's nothing to choose.
        int maxTemplateDim = templates.Count == 0 ? 0 : templates.Max(t => Math.Max(t.Gray.Width, t.Gray.Height));
        bool needsScaleSearch = maxTemplateDim > 64;
        int chosenSize;
        if (iconRenderSizeOverride > 0)
        {
            chosenSize = iconRenderSizeOverride;
            Console.WriteLine($"[scale] using --icon-render-size override: {chosenSize} px");
        }
        else if (needsScaleSearch)
        {
            chosenSize = SelectGlobalRenderSize(screenshot, templates, [12, 16, 20, 24, 30, 40, 56], threshold);
        }
        else
        {
            chosenSize = 0;  // 0 = use each template's native size
            Console.WriteLine("[scale] templates already at render size; using native per-icon dimensions");
        }

        // Final detection pass. Scale templates so the LARGEST source dimension
        // lands at the target render size. The "right" answer per visual
        // verification is min-dim scaling (PG renders all icons so the smaller
        // source dim = ~16 px), but empirical NCC behaviour on PG's noisy
        // terrain prefers max-dim scaled templates: they retain more of the
        // source asset's distinguishing detail and produce sharper NCC peaks
        // even when the template is slightly larger than the rendered icon.
        // Min-dim scaling (verified visually correct) consistently produced
        // worse RANSAC convergence than max-dim across multiple render sizes
        // — bilinear-resize artifact appears to dominate at small templates.
        var byType = new Dictionary<string, List<TypedDetection>>(StringComparer.Ordinal);
        var raw = new List<(IconMeta, Detection, int, int)>();
        foreach (var (icon, gray, alpha) in templates)
        {
            int rw, rh;
            // Per-icon override beats the global render-size rule entirely.
            // Useful when PG renders a sprite with an aspect ratio that doesn't
            // match the source asset (e.g. landmark_npc on Serbule).
            if (perIconOverrides.TryGetValue(icon.Name, out var forced))
            {
                rw = forced.W; rh = forced.H;
                Console.WriteLine($"  [icon] {icon.Name}: forced {rw}x{rh} via --icon-size override");
            }
            else if (chosenSize == 0)
            {
                rw = gray.Width; rh = gray.Height;
            }
            else
            {
                int maxDim = Math.Max(gray.Width, gray.Height);
                rw = Math.Max(1, gray.Width * chosenSize / maxDim);
                rh = Math.Max(1, gray.Height * chosenSize / maxDim);
            }
            var grayD = (rw == gray.Width && rh == gray.Height) ? gray : ImageOps.Resize(gray, rw, rh);
            var alphaD = (rw == alpha.Width && rh == alpha.Height) ? alpha : ImageOps.Resize(alpha, rw, rh);
            // 64-cap per template by score. Tried unlimited briefly and it
            // made things worse: hundreds of mid-score (~0.65) false-positive
            // teardrop patches entered the pool, and RANSAC found
            // wrong-but-self-consistent seeds with more "inliers" at lower
            // average score than the correct 7-inlier @ 0.85-score solution.
            // The cap acts as a quality filter — only the highest-confidence
            // NCC matches per template, which are mostly true icons.
            //
            // Trade-off: legitimate icons in dense clusters whose NCC score
            // is mediocre (e.g. small icons near other icons) may fall below
            // the top-64 by score and be missed. That's currently the v1
            // limitation; a smarter cap (e.g. top-N per spatial region) would
            // give clusters fair representation without flooding the pool.
            var hits = NccTemplateMatch.FindAll(screenshot, grayD, alphaD, threshold, maxResults: 64);
            Console.WriteLine($"  [icon] {icon.Name} @ {rw}x{rh}: {hits.Count} detections >= {threshold:0.00}" +
                              (hits.Count > 0 ? $" (top {hits[0].Score:0.000})" : ""));
            if (hits.Count == 0) continue;

            if (!byType.TryGetValue(icon.LandmarkType, out var list))
            {
                list = new List<TypedDetection>();
                byType[icon.LandmarkType] = list;
            }
            foreach (var h in hits)
            {
                raw.Add((icon, h, rw, rh));
                var (cx, cy) = h.Centre(rw, rh);
                var anchorX = cx + rw * (icon.PivotX - 0.5);
                var anchorY = cy + rh * (0.5 - icon.PivotY);
                list.Add(new TypedDetection(
                    IconName: icon.Name,
                    AnchorScreenshotX: anchorX,
                    AnchorScreenshotY: anchorY,
                    MatchScore: h.Score));
            }
        }
        return (byType, raw);
    }

    private static int SelectGlobalRenderSize(
        GrayImage screenshot,
        List<(IconMeta Icon, GrayImage Gray, GrayImage Alpha)> templates,
        int[] candidates,
        double threshold)
    {
        if (candidates.Length == 1)
        {
            Console.WriteLine($"[scale] single candidate {candidates[0]} px (templates already at render size)");
            return candidates[0];
        }

        int best = candidates[0];
        double bestEvidence = double.NegativeInfinity;
        Console.WriteLine($"[scale] sweeping {candidates.Length} render-size candidates across {templates.Count} templates");
        foreach (var target in candidates)
        {
            // Aggregate evidence at this candidate: sum of top score per icon
            // (above threshold). Captures "this scale gives at least one good
            // match per icon" without letting one super-matched icon dominate.
            double evidence = 0;
            int templatesWithHits = 0;
            foreach (var (_, gray, alpha) in templates)
            {
                // Max-dim scaling — see matching comment in the final pass.
                int maxDim = Math.Max(gray.Width, gray.Height);
                int rw = Math.Max(1, gray.Width * target / maxDim);
                int rh = Math.Max(1, gray.Height * target / maxDim);
                var grayD = ImageOps.Resize(gray, rw, rh);
                var alphaD = ImageOps.Resize(alpha, rw, rh);
                var top = NccTemplateMatch.FindBest(screenshot, grayD, alphaD, threshold);
                if (top is null) continue;
                evidence += top.Value.Score;
                templatesWithHits++;
            }
            Console.WriteLine($"[scale]   target={target}  evidence={evidence:0.000}  templatesWithHits={templatesWithHits}/{templates.Count}");
            if (evidence > bestEvidence)
            {
                bestEvidence = evidence;
                best = target;
            }
        }
        Console.WriteLine($"[scale] chose render size {best} px (aggregate evidence {bestEvidence:0.000})");
        return best;
    }

#pragma warning disable IDE0051  // kept for potential future per-type fallback
    private static IReadOnlyList<AssignedReference> AssignAndScore(
        List<TypedDetection> detections, List<LandmarkRef> areaRefs, MapRect mapRect, string typeName)
    {
        // v1 assignment policy:
        //   - dedup detections that pivot-corrected onto the same pixel,
        //   - trim to areaRefs.Count by best score (icon shape can score on noise),
        //   - pair sequentially with areaRefs.
        //
        // For single-landmark types (areaRefs.Count == 1) this is exact: the
        // best detection IS the landmark. For multi-landmark types the order
        // is the file order of landmarks.json, which is arbitrary — the solver's
        // handedness step absorbs simple swaps, but a real cluster of N
        // same-type landmarks in tight visual proximity may misassign. Issue
        // #852's "Same-type clustering" verification-owed item; a future
        // upgrade can swap this for a global-RANSAC sweep that uses every
        // type's detections jointly.

        var unique = new List<TypedDetection>();
        foreach (var d in detections.OrderByDescending(d => d.MatchScore))
        {
            if (unique.Any(u => Math.Abs(u.AnchorScreenshotX - d.AnchorScreenshotX) < 4 &&
                                Math.Abs(u.AnchorScreenshotY - d.AnchorScreenshotY) < 4))
                continue;
            unique.Add(d);
        }
        detections = unique;

        if (detections.Count > areaRefs.Count)
        {
            detections = [.. detections.OrderByDescending(d => d.MatchScore).Take(areaRefs.Count)];
        }

        var result = new List<AssignedReference>(detections.Count);
        for (int i = 0; i < detections.Count; i++)
        {
            var (tx, ty) = mapRect.ScreenshotToTexture(detections[i].AnchorScreenshotX, detections[i].AnchorScreenshotY);
            result.Add(new AssignedReference(
                Label: $"{typeName}:{areaRefs[i].Name} ({detections[i].IconName})",
                WorldX: areaRefs[i].World.X,
                WorldZ: areaRefs[i].World.Z,
                PixelX: tx,
                PixelY: ty,
                MatchScore: detections[i].MatchScore));
        }
        return result;
    }

    private sealed record TypedDetection(string IconName, double AnchorScreenshotX, double AnchorScreenshotY, double MatchScore);
#pragma warning restore IDE0051
}
