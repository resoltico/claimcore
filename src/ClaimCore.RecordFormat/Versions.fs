namespace ClaimCore.RecordFormat

open ClaimCore.Contract

/// A format number identifies one byte grammar and its admissible value set.
module RecordVersions =
    [<Literal>]
    let CaseRequest = 5

    [<Literal>]
    let Snapshot = 4

    [<Literal>]
    let Authority = 2

    [<Literal>]
    let LedgerEntry = 2

    [<Literal>]
    let CaseReferenceIdentity = 2
