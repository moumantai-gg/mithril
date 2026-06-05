using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Internal;

/// <summary>
/// Phase 6 (mithril#1076): when a Schema-1 user-refinement record is loaded
/// from disk &#8212; i.e. the JSON entry has no <c>"frame"</c> property &#8212;
/// the load path infers <see cref="AreaCalibration.Frame"/> from the record's
/// <see cref="CalibrationSource"/> per the spec §7.2 table.
///
/// <para>The Schema-1 default for <see cref="CalibrationSource.UserRefinement"/>
/// is <see cref="CalibrationFrame.Overlay"/> because AutoCal has never shipped
/// in a tagged release &#8212; every in-the-wild Schema-1 <c>UserRefinement</c>
/// record is Legolas-wizard-produced (overlay-frame). <see cref="CalibrationSource.AutoCapture"/>
/// is texture-frame (the AutoCal-RANSAC fit lives in base-texture pixels).
/// Unknown/aspirational sources (<see cref="CalibrationSource.CommunitySync"/>,
/// future enum values) default to Overlay with a one-time warn-log per record so
/// they cannot be silently fed to AutoCal's texture-frame drift-check.</para>
/// </summary>
public sealed class UserRefinementStoreFrameInferenceTests : IDisposable
{
    private readonly string _dir;

    public UserRefinementStoreFrameInferenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mithril-refstore-frame-infer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* CI temp dir gets reaped */ }
    }

    private string Path_ => Path.Combine(_dir, "refinements.json");

    private const string V1JsonTemplate = """
        {
          "calibrations": {
            "Map_AreaSerbule": {
              "scale": 0.82, "rotationRadians": 0.0, "originX": 100.0, "originY": 200.0,
              "referenceCount": 4, "residualPixels": 0.5,
              "source": "{SOURCE}", "schemaVersion": 1, "calibrationZoom": 1.0, "mirrorNorth": false
            }
          }
        }
        """;

    [Theory]
    [InlineData("UserRefinement", CalibrationFrame.Overlay)]
    [InlineData("AutoCapture", CalibrationFrame.Texture)]
    public void Schema1_NoFrameField_InfersFromSource(string source, CalibrationFrame expected)
    {
        File.WriteAllText(Path_, V1JsonTemplate.Replace("{SOURCE}", source));

        var store = new UserRefinementStore(_dir);

        // mithril#1082: TryGetAny returns the scene's typed slots; the inferred
        // Frame is observable as which slot the record landed in.
        store.TryGetAny("Map_AreaSerbule", out var slots).Should().BeTrue();
        var cal = slots.Get(expected);
        cal.Should().NotBeNull();
        cal!.Frame.Should().Be(expected);
    }

    [Fact]
    public void Schema1_CommunitySyncSource_DefaultsToOverlay_AndWarnLogs()
    {
        // CommunitySync is aspirational (spec §7.2 / P.2); no consumer ships today.
        // Until the consumer + aggregator land, default to Overlay (the safer
        // assumption — won't be silently fed to AutoCal's texture-frame drift-check)
        // and emit a one-time warn-log so the developer notices.
        File.WriteAllText(Path_, V1JsonTemplate.Replace("{SOURCE}", "CommunitySync"));

        var logger = new RecordingLogger();
        // NOTE: store hand-stamps non-(UserRefinement|AutoCapture) sources back to UserRefinement on
        // load (defensive against bundled / community records ending up in the user store), so the
        // surviving record's Source ends up UserRefinement; the FRAME inference however still
        // distinguishes the CommunitySync inbound value because we run it BEFORE the Source
        // restamping. Verify the Overlay-with-warn outcome rather than the Source value.
        var store = new UserRefinementStore(_dir, logger);

        store.TryGetAny("Map_AreaSerbule", out var slots).Should().BeTrue();
        slots.Overlay.Should().NotBeNull();
        slots.Overlay!.Frame.Should().Be(CalibrationFrame.Overlay);
        slots.Texture.Should().BeNull();
        logger.Warnings.Should().Contain(w => w.Contains("CommunitySync", StringComparison.Ordinal));
    }

    [Fact]
    public void Schema1_UnknownSource_DefaultsToOverlay_AndWarnLogs()
    {
        // An unknown source value (forward-compat / future enum) cannot be a clean
        // texture-frame default; biased to Overlay + warn so it doesn't silently
        // feed AutoCal's drift-check.
        File.WriteAllText(Path_, V1JsonTemplate.Replace("{SOURCE}", "FutureSource_DoesNotExist"));

        var logger = new RecordingLogger();
        var store = new UserRefinementStore(_dir, logger);

        // The whole entry may degrade to "skipped" if the enum NAME doesn't parse
        // (UseStringEnumConverter throws on unknown names) — that path is already
        // covered by the per-entry resilient parse + skip+warn. If the entry DOES
        // survive (e.g. a future build relaxes the parse), the record lands in
        // the Overlay slot per the unknown-source inference rule.
        if (store.TryGetAny("Map_AreaSerbule", out var slots))
        {
            slots.Overlay?.Frame.Should().Be(CalibrationFrame.Overlay);
        }

        // Either way, a warn must have been logged: either the per-entry unparseable
        // log OR the unknown-source frame-inference warn.
        logger.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public void Schema2_ExplicitFrameField_PreservedVerbatim()
    {
        // A Schema-2 record that already carries "frame" is loaded as-is — no
        // inference, no warn. UserRefinement + Overlay is the Legolas-wizard
        // round-trip case.
        const string v2 = """
            {
              "schemaVersion": 2,
              "calibrations": {
                "Map_AreaSerbule": {
                  "scale": 0.82, "rotationRadians": 0.0, "originX": 100.0, "originY": 200.0,
                  "referenceCount": 4, "residualPixels": 0.5,
                  "source": "UserRefinement", "schemaVersion": 1, "calibrationZoom": 1.0,
                  "mirrorNorth": false, "frame": "Overlay"
                }
              }
            }
            """;
        File.WriteAllText(Path_, v2);

        var store = new UserRefinementStore(_dir);

        store.TryGet("Map_AreaSerbule", CalibrationFrame.Overlay, out var cal).Should().BeTrue();
        cal.Frame.Should().Be(CalibrationFrame.Overlay);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
