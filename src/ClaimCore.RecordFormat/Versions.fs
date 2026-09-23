namespace ClaimCore.RecordFormat

/// Independent compatibility revisions for wire requests and retained operation data.
module RecordVersions =
    [<Literal>]
    let CanonicalCommandFormat = 3

    [<Literal>]
    let RequestFingerprint = 1

    [<Literal>]
    let Snapshot = 2
