namespace ClaimCore.RecordFormat

/// Independent byte-format identities for durable records.
module RecordVersions =
    [<Literal>]
    let CanonicalCommandFormat = 3

    [<Literal>]
    let RequestFingerprint = 1

    [<Literal>]
    let Snapshot = 2

    [<Literal>]
    let LedgerEntry = 1

    [<Literal>]
    let CaseReferenceIdentity = 1
