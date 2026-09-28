namespace ClaimCore.Database

[<RequireQualifiedAccess>]
type internal RestoreRecheckFailure =
    | TrustAnchorUnavailable
    | ReportInvalid
    | EvidenceIndexInvalid
    | EvidenceMismatch

[<NoEquality; NoComparison>]
type internal RestoreRecheckResult =
    {
        Nonce: string
        ReportSha256: string
        EvidenceIndexSha256: string
        WitnessCutoff: int64
        WitnessCutoffHash: string
    }
