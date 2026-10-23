namespace ClaimCore.RecordFormat

open ClaimCore.Contract

/// Permanent retained payload admission, independent of current business-text policy.
module FormatLimits =
    [<Literal>]
    let MaximumFreeTextBytes = ClaimCore.Contract.TextFormat.MaximumFreeTextBytes

    [<Literal>]
    let MaximumPayloadBytes = 32768
