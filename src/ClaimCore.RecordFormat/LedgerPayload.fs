namespace ClaimCore.RecordFormat

type LedgerPayload =
    {
        Request: byte array
        Snapshot: byte array
    }

/// The durable request-plus-snapshot payload budget, independent of wire limits.
module LedgerPayload =
    [<Literal>]
    let MaximumBytes = 32768
