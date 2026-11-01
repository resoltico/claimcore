namespace ClaimCore.RecordFormat


/// A format number identifies one byte grammar and its admissible value set.
module RecordVersions =
    [<Literal>]
    let Control = 2

    [<Literal>]
    let CaseRequest = 3

    [<Literal>]
    let Snapshot = 3

    [<Literal>]
    let Authority = 2

    [<Literal>]
    let LedgerEntry = 3
