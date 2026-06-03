using System;
using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Per-attempt <see cref="IIconTemplateProvider"/> over the on-disk icon-template
/// cache the out-of-process asset-extractor sidecar (issue #931) populates. The
/// icon analogue of <see cref="CachedBaseTextureProvider"/>: each
/// <see cref="GetTemplates"/> reflects whatever is on disk now, so a same-session
/// populate (the engine's <c>--icons</c> demand-trigger, or the startup warm-up)
/// engages on the very next attempt — no app restart (#949).
///
/// <para><b>Caching.</b> Decompressing + parsing the icon blob on every attempt is
/// wasteful, so the loaded set is memoised keyed by the manifest's
/// <c>pixelSha256</c> (<see cref="BundledIconTemplateLoader.ManifestPixelSha256"/>,
/// a cheap manifest-only read). The set is reloaded when that hash changes <i>or</i>
/// when the previously-cached set was <see cref="IconTemplateSet.Empty"/> — so a
/// fresh same-session populate (cache went empty → populated) is always picked up
/// even if the manifest read transiently raced the blob write.</para>
///
/// <para><b>Fail-soft:</b> any miss/exception → <see cref="IconTemplateSet.Empty"/>
/// (never throws into the engine). Empty templates → no typed detections → the
/// confidence gate rejects → safe-degrade.</para>
/// </summary>
internal sealed class CachedIconTemplateProvider : IIconTemplateProvider
{
    private readonly string _cacheDir;
    private readonly ILogger? _logger;
    private readonly object _gate = new();

    // Memoised load. _cachedHash is the manifest pixelSha256 the cached set was
    // loaded from; null means "nothing loaded yet". Guarded by _gate.
    private IconTemplateSet? _cached;
    private string? _cachedHash;

    /// <param name="cacheDir">Directory holding <c>icon-templates.{json,bin}</c>.</param>
    public CachedIconTemplateProvider(string cacheDir, ILogger? logger = null)
    {
        _cacheDir = cacheDir;
        _logger = logger;
    }

    public IconTemplateSet GetTemplates()
    {
        try
        {
            var currentHash = BundledIconTemplateLoader.ManifestPixelSha256(_cacheDir, _logger);

            lock (_gate)
            {
                // Reload when: nothing cached yet, the manifest hash changed, OR the
                // cached set was Empty (so a same-session populate is picked up — a
                // populated cache after an Empty load must re-read regardless of hash).
                bool stale =
                    _cached is null
                    || _cached.Templates.Count == 0
                    || !string.Equals(_cachedHash, currentHash, StringComparison.OrdinalIgnoreCase);

                if (stale)
                {
                    _cached = BundledIconTemplateLoader.LoadFromDirectory(_cacheDir, _logger);
                    _cachedHash = currentHash;
                }

                return _cached ?? IconTemplateSet.Empty;
            }
        }
        catch (Exception ex)
        {
            // Defensive: the loader is itself fail-soft, but the manifest probe could
            // throw on an exotic IO fault. Never let that reach the engine.
            _logger?.LogWarning(ex, "Icon-template provider threw resolving {CacheDir}; returning Empty (safe-degrade).", _cacheDir);
            return IconTemplateSet.Empty;
        }
    }
}
