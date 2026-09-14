namespace ClaimCore.Postgres

open ClaimCore.Application

/// The configured technical retention bounds for exact request recovery material.
[<NoEquality; NoComparison>]
type internal PreparationLimits =
    {
        MaximumPreparations: int
        MaximumCanonicalRequestBytes: int64
        MaximumPageSize: int
        MaximumAttemptsPerOperation: int
    }

/// PostgreSQL tokens for Application-owned preparation provenance.
module internal PreparingContractKindEncoding =
    let token value =
        match value with
        | PreparingContractKind.LegacyUnclassified -> "LEGACY_UNCLASSIFIED"
        | PreparingContractKind.SemanticCoreV1 -> "SEMANTIC_CORE_V1"

    let parse value =
        match value with
        | "LEGACY_UNCLASSIFIED" -> Some PreparingContractKind.LegacyUnclassified
        | "SEMANTIC_CORE_V1" -> Some PreparingContractKind.SemanticCoreV1
        | _ -> None

/// Owner-only retention maintenance parameters constrained to reviewed operational bounds.
[<NoEquality; NoComparison>]
type PreparationPruneOptions =
    {
        SettledRetentionDays: int
        AbandonedRetentionDays: int
        BatchLimit: int
        DryRun: bool
    }

[<NoEquality; NoComparison>]
type PreparationPruneResult =
    {
        CandidateCount: int
        DeletedCount: int
        DryRun: bool
        TerminalPreparationCount: int64
        TerminalCanonicalRequestBytes: int64
    }

module internal PreparationLimits =
    let defaults =
        {
            MaximumPreparations = 1024
            MaximumCanonicalRequestBytes = 64L * 1024L * 1024L
            MaximumPageSize = 100
            MaximumAttemptsPerOperation = 64
        }

    let validate limits =
        if
            limits.MaximumPreparations < 1
            || limits.MaximumPreparations > 1024
            || limits.MaximumCanonicalRequestBytes < 1L
            || limits.MaximumCanonicalRequestBytes > 64L * 1024L * 1024L
            || limits.MaximumPageSize < 1
            || limits.MaximumPageSize > 100
            || limits.MaximumAttemptsPerOperation < 1
            || limits.MaximumAttemptsPerOperation > 64
        then
            invalidArg
                (nameof limits)
                "Preparation limits must be positive and no greater than the reviewed defaults."

module PreparationPruneOptions =
    let defaults =
        {
            SettledRetentionDays = 30
            AbandonedRetentionDays = 30
            BatchLimit = 100
            DryRun = false
        }

    let validate options =
        if
            options.SettledRetentionDays < 1
            || options.SettledRetentionDays > 3650
            || options.AbandonedRetentionDays < 1
            || options.AbandonedRetentionDays > 3650
            || options.BatchLimit < 1
            || options.BatchLimit > 1000
        then
            invalidArg
                (nameof options)
                "Retention periods and maintenance batches must stay within the reviewed bounds."
