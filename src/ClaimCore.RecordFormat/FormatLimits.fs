namespace ClaimCore.RecordFormat

/// Permanent retained payload admission, independent of current business-text policy.
module FormatLimits =
    [<Literal>]
    let MaximumFreeTextBytes = ClaimCore.Domain.TextFormat.MaximumFreeTextBytes

    [<Literal>]
    let MaximumPayloadBytes = 32768
