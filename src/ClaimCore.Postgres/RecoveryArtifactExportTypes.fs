namespace ClaimCore.Postgres

open System

[<NoEquality; NoComparison>]
type internal RecoveryArtifactIssueKey =
    {
        Id: Guid
        IssueFrom: DateTimeOffset
        IssueUntil: DateTimeOffset
        Lifetime: TimeSpan
        MaximumExports: int
    }
