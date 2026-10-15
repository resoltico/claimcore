namespace ClaimCore.RecordFormat

/// A format number changes only with its durable bytes.
module RecordVersions =
    [<Literal>]
    let CaseRequest = 4

    [<Literal>]
    let Snapshot = 3

    [<Literal>]
    let Authority = 2

    [<Literal>]
    let LedgerEntry = 2

    [<Literal>]
    let CaseReferenceIdentity = 2
