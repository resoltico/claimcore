namespace ClaimCore.RecordFormat

open ClaimCore.Contract

/// A format number identifies one byte grammar and its admissible value set.
module RecordVersions =
    [<Literal>]
    let CaseRequest = 1

    [<Literal>]
    let Snapshot = 1

    [<Literal>]
    let Authority = 1

    [<Literal>]
    let LedgerEntry = 1

    [<Literal>]
    let CaseReferenceIdentity = 1
