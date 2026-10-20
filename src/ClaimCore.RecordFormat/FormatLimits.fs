namespace ClaimCore.RecordFormat

open ClaimCore.Catalog

/// Permanent retained payload admission, independent of current business-text policy.
module FormatLimits =
    [<Literal>]
    let MaximumFreeTextBytes = ClaimCore.Catalog.TextFormat.MaximumFreeTextBytes

    [<Literal>]
    let MaximumPayloadBytes = 32768
