namespace ClaimCore.Database

open ClaimCore.Postgres
open ClaimCore.Witness

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type internal VerifyDataOutcome =
    | Verified of DataAuditSummary * Snapshot
    | InputRefused of DatabaseInputProblem
    | AuditFailed of VerifyDataFailure

and [<RequireQualifiedAccess; NoEquality; NoComparison>] internal VerifyDataFailure =
    | EvidenceDivergence
    | AuditUnavailable
    | AuditFault
    | TopologyRefused
