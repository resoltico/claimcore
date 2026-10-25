namespace ClaimCore.RecordFormat

open ClaimCore.Contract

/// Permanent retained payload admission, independent of current business-text policy.
module FormatLimits =
    [<Literal>]
    let MaximumFreeTextBytes = Formats.MaximumFreeTextBytes

    [<Literal>]
    let MaximumPayloadBytes = Formats.MaximumPayloadBytes
