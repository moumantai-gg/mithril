using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibration.Common;
using Mithril.Tools.MapCalibration.Harness;
using Xunit;

namespace Mithril.Tools.MapCalibration.Harness.Tests;

public class FakeMethodIntegrationTests
{
    private const double Scale = 2.0;
    private static readonly double Rot = 0.4;
    private static readonly TexturePixel Origin = new(100, 200);

    private static TexturePixel Project(double x, double z)
    {
        var cos = Math.Cos(Rot);
        var sin = Math.Sin(Rot);
        var rotE = x * cos + z * sin;
        var rotN = -x * sin + z * cos;
        return new TexturePixel(Origin.X + Scale * rotE, Origin.Y - Scale * rotN);
    }

    private static readonly (double X, double Z)[] World =
    {
        (10.0, 0.0),
        (0.0, 12.0),
        (-7.0, 5.0),
        (4.0, -9.0),
    };

    /// <summary>
    /// Trivial in-test method: emits a fixed candidate batch through the sink on
    /// activation. Proves the <see cref="ICalibrationMethod"/> +
    /// <see cref="ICandidateSink"/> contract end-to-end, headlessly.
    /// </summary>
    private sealed class FakeBatchMethod : ICalibrationMethod
    {
        private readonly IReadOnlyList<CandidateRef> _batch;

        public FakeBatchMethod(IReadOnlyList<CandidateRef> batch) => _batch = batch;

        public string Name => "Fake";

        public string Description => "Emits a fixed batch of candidates.";

        public object? ConfigView => null;

        public IDisposable Activate(CalibrationContext ctx, ICandidateSink sink)
        {
            sink.EmitBatch(_batch);
            return new NoopDisposable();
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    [Fact]
    public void Fake_method_emits_through_sink_and_session_solves()
    {
        var landmarks = World
            .Select((p, i) => new LandmarkRef("Npc", $"L{i}", new WorldCoord(p.X, 0, p.Z)))
            .ToList();
        var ctx = new CalibrationContext("TestArea", landmarks, null, null, (640, 480), null);
        var session = new CalibrationSession(ctx);

        var batch = World.Select(p => new CandidateRef(
            Project(p.X, p.Z),
            new WorldCoord(p.X, 0, p.Z),
            LandmarkId: null, SuggestedName: "lm", Kind: "Npc",
            Source: CalibrationRefSource.Ncc, Confidence: 0.9)).ToList();

        var method = new FakeBatchMethod(batch);
        using var handle = method.Activate(ctx, session);

        session.Refs.Should().HaveCount(4);
        session.Calibration.Should().NotBeNull();
        session.Calibration!.Scale.Should().BeApproximately(Scale, 1e-6);
        session.Calibration.ResidualPixels.Should().BeApproximately(0, 1e-6);
        session.Projections.Should().HaveCount(4);
    }

    [Fact]
    public void ConfigView_is_null_in_headless_use()
    {
        var method = new FakeBatchMethod(System.Array.Empty<CandidateRef>());
        method.ConfigView.Should().BeNull();
    }
}
