namespace ClaimCore.Postgres

/// Owner-only diagnostic meaning. Never contains SQL, paths, connection strings or provider text.
[<RequireQualifiedAccess>]
type AdministrationFailure =
    | OwnerConnectionInvalid
    | OwnerIdentityRejected
    | PostgresVersionUnsupported
    | DatabaseConfigurationInvalid
    | CatalogUnreadable
    | MigrationNewerThanRuntime
    | MigrationIdentityMismatch
    | MigrationJournalWriteFailed
    | JournalWithoutSchema
    | SchemaWithoutJournal
    | MigrationsPending
    | BusinessZoneInvalid
    | BusinessZoneAlreadyConfigured
    | InstallationLineageMissing
    | BusinessZoneTypeInvalid
    | BusinessZoneWriteFailed
    | PruneOptionsInvalid
    | PruneAuditFailed
    | RecoveryFootprintUnreadable
    | SchemaDefinitionInvalid
    | DatabaseUnavailable
    | OperationFailed
    | CommitUnconfirmed

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type AdministrationOutcome<'value> =
    | Completed of 'value
    | NotStarted of AdministrationFailure
    | NotCommitted of AdministrationFailure
    | CompletionUnknown of AdministrationFailure
    | CompletedCleanupFailed of 'value

exception internal AdministrationException of AdministrationFailure

module internal AdministrationFailures =
    let refuse reason = raise (AdministrationException reason)

module AdministrationOutcome =
    let map transform outcome =
        match outcome with
        | AdministrationOutcome.Completed value -> AdministrationOutcome.Completed(transform value)
        | AdministrationOutcome.CompletedCleanupFailed value ->
            AdministrationOutcome.CompletedCleanupFailed(transform value)
        | AdministrationOutcome.NotStarted reason -> AdministrationOutcome.NotStarted reason
        | AdministrationOutcome.NotCommitted reason -> AdministrationOutcome.NotCommitted reason
        | AdministrationOutcome.CompletionUnknown reason ->
            AdministrationOutcome.CompletionUnknown reason
