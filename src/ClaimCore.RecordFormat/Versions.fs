namespace ClaimCore.RecordFormat


/// A format number identifies one byte grammar and its admissible value set.
module RecordVersions =
    [<Literal>]
    let CaseRequest = 2

    [<Literal>]
    let Snapshot = 2

    [<Literal>]
    let Authority = 2

    [<Literal>]
    let LedgerEntry = 2
