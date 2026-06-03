using System.Collections.Generic;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// The decoded set of icon templates the detector matches against. Injected into
/// the detector (it never loads templates itself); the default provider is
/// <see cref="Internal.BundledIconTemplateLoader"/>, which ships them pre-decoded
/// so the in-process core needs no image decoder.
/// </summary>
public sealed class IconTemplateSet
{
    public IReadOnlyList<IconTemplate> Templates { get; }

    public IconTemplateSet(IReadOnlyList<IconTemplate> templates)
    {
        Templates = templates;
    }

    public static IconTemplateSet Empty { get; } = new(new List<IconTemplate>());
}
