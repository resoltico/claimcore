namespace ClaimCore.RecordFormat

open ClaimCore.Contract

/// A format number identifies one byte grammar and its admissible value set.
module RecordVersions =
    [<Literal>]
    let CaseRequest = 6

    [<Literal>]
    let Snapshot = 4

    [<Literal>]
    let Authority = 3

    [<Literal>]
    let LedgerEntry = 2

    [<Literal>]
    let CaseReferenceIdentity = 2
