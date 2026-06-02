using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal static class SynthesisProbeTracer
{
    public const string ActivitySourceName = "Mithril.Tools.MapCalibrationSynthesisProbe";
    public static readonly ActivitySource Source = new(ActivitySourceName);

    public static TracerProvider? Configure(bool traceConsole, string? otlpEndpoint)
    {
        if (!traceConsole && otlpEndpoint is null) return null;

        var builder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("MapCalibrationSynthesisProbe"))
            .AddSource(ActivitySourceName);

        if (traceConsole) builder.AddConsoleExporter();
        if (otlpEndpoint is not null)
            builder.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));

        return builder.Build();
    }
}
