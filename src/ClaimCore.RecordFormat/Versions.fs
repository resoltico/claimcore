namespace ClaimCore.RecordFormat

open ClaimCore.Contract

/// A format number identifies one byte grammar and its admissible value set.
module RecordVersions =
    [<Literal>]
    let CaseRequest = 7

    [<Literal>]
    let Snapshot = 5

    [<Literal>]
    let Authority = 4

    [<Literal>]
    let LedgerEntry = 3

    [<Literal>]
    let CaseReferenceIdentity = 2
