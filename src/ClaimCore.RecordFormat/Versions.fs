namespace ClaimCore.RecordFormat


/// A format number identifies one byte grammar and its admissible value set.
module RecordVersions =
    [<Literal>]
    let CaseRequest = 4

    [<Literal>]
    let Snapshot = 4

    [<Literal>]
    let Authority = 3

    [<Literal>]
    let LedgerEntry = 6

    [<Literal>]
    let Work = 1

    [<Literal>]
    let Reminder = 1
