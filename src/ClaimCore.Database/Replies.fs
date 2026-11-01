namespace ClaimCore.Database

open System
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Application
open ClaimCore.Postgres

type OwnerAcceptance =
    {
        OperationId: OperationId
        TargetActorId: ActorId
        Position: LedgerPosition
    }

[<RequireQualifiedAccess; NoComparison>]
type DatabaseSuccess =
    | Initialized of Guid
    | Rebuilt of ProjectionRebuildReport
    | RestoredSessionsInvalidated of int64
    | Head of LedgerPosition
    | Status of InstallationStatus
    | Maintenance of MaintenanceObservation
    | SessionsPruned of SessionPruneReport
    | Accepted of OwnerAcceptance
    | AlreadyAccepted of OwnerAcceptance
    | Verified of LedgerPosition * elapsedMilliseconds: int64

[<RequireQualifiedAccess; NoComparison>]
type Reply =
    | Operation of Outcome<DatabaseSuccess>
    | Cancelled
    | OutcomeUnknown of OperationId option
    | Diverged of VerificationDivergence * elapsedMilliseconds: int64
    | Usage of UsageFrame
    | Help of HelpFrame
    | Version of VersionFrame
    | MaintenanceConflict of MaintenanceObservation
    | MaintenanceRequired

/// Administrative successes share failures with other public outcomes; uncertainty remains distinct.
module Replies =
    open Codec

    let private acceptance =
        record (fun operation target position ->
            {
                OperationId = operation
                TargetActorId = target
                Position = position
            })
        |> field "operationId" _.OperationId ResponseScalars.operationId
        |> field "targetActorId" _.TargetActorId ResponseScalars.actorId
        |> field "position" _.Position ResponseCodecs.position

    // An administrative head may be genesis; accepted receipts still require a positive sequence.
    let private administrativePosition =
        (record (fun sequence hash ->
            {
                LedgerPosition.Sequence = sequence
                EntryHash = hash
            })
        : Fields<LedgerPosition, _>)
        |> field
            "sequence"
            _.Sequence
            (integer 0L SafeInteger.Maximum
             |> map (Sequence.create >> Option.get) Sequence.value)
        |> field
            "entryHash"
            _.EntryHash
            (ResponseScalars.digest
             |> map
                 (Convert.FromHexString >> EntryHash.ofBytes >> Option.get)
                 (EntryHash.toBytes >> Convert.ToHexStringLower))
        |> build

    let private divergenceCodec =
        (record (fun sequence finding ->
            {
                VerificationDivergence.Sequence = sequence
                Finding = finding
            })
        : Fields<VerificationDivergence, _>)
        |> field
            "sequence"
            _.Sequence
            (nullable (
                integer 0L SafeInteger.Maximum
                |> map (Sequence.create >> Option.get) Sequence.value
            ))
        |> field "finding" _.Finding (enumeration VerificationFinding.all)
        |> build

    let private rebuildFields =
        (record (fun position divergence ->
            {
                ProjectionRebuildReport.Position = position
                PreviousDivergence = divergence
            })
        : Fields<ProjectionRebuildReport, _>)
        |> field "position" _.Position administrativePosition
        |> field "previousDivergence" _.PreviousDivergence (nullable divergenceCodec)

    let private maintenanceFields: Fields<MaintenanceObservation, _> =
        record (fun state instant position changed ->
            {
                MaintenanceObservation.State = state
                ObservedAt = instant
                Position = position
                Changed = changed
            })
        |> field
            "maintenance"
            (fun (value: MaintenanceObservation) -> value.State)
            ResponseCodecs.registerMaintenance
        |> field "observedAt" _.ObservedAt instant
        |> field "position" _.Position administrativePosition
        |> field "changed" _.Changed boolean

    let private formats: Codec<SupportedRecordFormats> =
        record (fun request snapshot authority control ledger ->
            {
                SupportedRecordFormats.CaseRequest = request
                Snapshot = snapshot
                Authority = authority
                Control = control
                LedgerEntry = ledger
            })
        |> field "caseRequest" _.CaseRequest (integer 1L 32767L |> map int int64)
        |> field "snapshot" _.Snapshot (integer 1L 32767L |> map int int64)
        |> field "authority" _.Authority (integer 1L 32767L |> map int int64)
        |> field "control" _.Control (integer 1L 32767L |> map int int64)
        |> field "ledgerEntry" _.LedgerEntry (integer 1L 32767L |> map int int64)
        |> build

    let private countCodec = integer 0L SafeInteger.Maximum

    let private statusFields: Fields<InstallationStatus, _> =
        record
            (fun
                installation
                baseline
                digest
                zone
                instant
                position
                maintenance
                formats
                actors
                cases
                fences
                sessions
                expired ->
                {
                    InstallationStatus.InstallationId = installation
                    Baseline = baseline
                    BaselineSha256 = digest
                    BusinessTimeZone = zone
                    ObservedAt = instant
                    Position = position
                    Maintenance = maintenance
                    Formats = formats
                    Actors = actors
                    Cases = cases
                    Fences = fences
                    Sessions = sessions
                    ExpiredSessions = expired
                })
        |> field "installationId" (fun (value: InstallationStatus) -> value.InstallationId) uuid
        |> field "baseline" _.Baseline (boundedText 1 64)
        |> field "baselineSha256" _.BaselineSha256 ResponseScalars.digest
        |> field "businessTimeZone" _.BusinessTimeZone (boundedText 1 128)
        |> field "observedAt" _.ObservedAt instant
        |> field "position" _.Position administrativePosition
        |> field "maintenance" _.Maintenance ResponseCodecs.registerMaintenance
        |> field "formats" _.Formats formats
        |> field "actors" _.Actors countCodec
        |> field "cases" _.Cases countCodec
        |> field "fences" _.Fences countCodec
        |> field "sessions" _.Sessions countCodec
        |> field "expiredSessions" _.ExpiredSessions countCodec

    let private pruneFields: Fields<SessionPruneReport, _> =
        record (fun instant limit expired deleted ->
            {
                SessionPruneReport.ObservedAt = instant
                Limit = limit
                ExpiredAtObservation = expired
                Deleted = deleted
            })
        |> field "observedAt" (fun (value: SessionPruneReport) -> value.ObservedAt) instant
        |> field "limit" _.Limit (integer 1L (int64 SessionPruning.MaximumBatch) |> map int int64)
        |> field "expiredAtObservation" _.ExpiredAtObservation countCodec
        |> field "deleted" _.Deleted countCodec

    // This closed codec table pairs every success constructor with its inverse projection; it has no nested decisions.
    // fsharplint:disable-next-line CyclomaticComplexity
    let private successVariants =
        [
            variant
                "STATUS"
                (DatabaseSuccess.Status >> Outcome.Succeeded >> Reply.Operation)
                (function
                | Reply.Operation(Outcome.Succeeded(DatabaseSuccess.Status value)) -> Some value
                | _ -> None)
                statusFields
            variant
                "MAINTENANCE"
                (DatabaseSuccess.Maintenance >> Outcome.Succeeded >> Reply.Operation)
                (function
                | Reply.Operation(Outcome.Succeeded(DatabaseSuccess.Maintenance value)) ->
                    Some value
                | _ -> None)
                maintenanceFields
            variant
                "SESSIONS_PRUNED"
                (DatabaseSuccess.SessionsPruned >> Outcome.Succeeded >> Reply.Operation)
                (function
                | Reply.Operation(Outcome.Succeeded(DatabaseSuccess.SessionsPruned value)) ->
                    Some value
                | _ -> None)
                pruneFields
            variant
                "REBUILT"
                (DatabaseSuccess.Rebuilt >> Outcome.Succeeded >> Reply.Operation)
                (function
                | Reply.Operation(Outcome.Succeeded(DatabaseSuccess.Rebuilt report)) -> Some report
                | _ -> None)
                rebuildFields
            variant
                "RESTORED_SESSIONS_INVALIDATED"
                (DatabaseSuccess.RestoredSessionsInvalidated
                 >> Outcome.Succeeded
                 >> Reply.Operation)
                (function
                | Reply.Operation(Outcome.Succeeded(DatabaseSuccess.RestoredSessionsInvalidated count)) ->
                    Some count
                | _ -> None)
                (record id |> field "count" id (integer 0L SafeInteger.Maximum))
            variant
                "INITIALIZED"
                (DatabaseSuccess.Initialized >> Outcome.Succeeded >> Reply.Operation)
                (function
                | Reply.Operation(Outcome.Succeeded(DatabaseSuccess.Initialized id)) -> Some id
                | _ -> None)
                (record id |> field "installationId" id uuid)
            variant
                "HEAD"
                (DatabaseSuccess.Head >> Outcome.Succeeded >> Reply.Operation)
                (function
                | Reply.Operation(Outcome.Succeeded(DatabaseSuccess.Head position)) -> Some position
                | _ -> None)
                (record id |> field "position" id administrativePosition)
            variant
                "ACCEPTED"
                (DatabaseSuccess.Accepted >> Outcome.Succeeded >> Reply.Operation)
                (function
                | Reply.Operation(Outcome.Succeeded(DatabaseSuccess.Accepted value)) -> Some value
                | _ -> None)
                acceptance
            variant
                "ALREADY_ACCEPTED"
                (DatabaseSuccess.AlreadyAccepted >> Outcome.Succeeded >> Reply.Operation)
                (function
                | Reply.Operation(Outcome.Succeeded(DatabaseSuccess.AlreadyAccepted value)) ->
                    Some value
                | _ -> None)
                acceptance
            variant
                "VERIFIED"
                (DatabaseSuccess.Verified >> Outcome.Succeeded >> Reply.Operation)
                (function
                | Reply.Operation(Outcome.Succeeded(DatabaseSuccess.Verified(position, elapsed))) ->
                    Some(position, elapsed)
                | _ -> None)
                (record (fun position elapsed -> position, elapsed)
                 |> field "position" fst administrativePosition
                 |> field "elapsedMilliseconds" snd (integer 0L SafeInteger.Maximum))
        ]

    let private operationVariants =
        [
            variant
                "MAINTENANCE_CONFLICT"
                Reply.MaintenanceConflict
                (function
                | Reply.MaintenanceConflict value -> Some value
                | _ -> None)
                maintenanceFields
            unitVariant "MAINTENANCE_REQUIRED" Reply.MaintenanceRequired
            variant
                "REJECTED"
                (Outcome.Rejected >> Reply.Operation)
                (function
                | Reply.Operation(Outcome.Rejected diagnostic) -> Some diagnostic
                | _ -> None)
                (record id |> field "diagnostic" id DiagnosticCodecs.diagnostic)
            variant
                "FAILED"
                (Outcome.Failed >> Reply.Operation)
                (function
                | Reply.Operation(Outcome.Failed fault) -> Some fault
                | _ -> None)
                (record id |> field "fault" id DiagnosticCodecs.fault)
            unitVariant "CANCELLED" Reply.Cancelled
            variant
                "OUTCOME_UNKNOWN"
                Reply.OutcomeUnknown
                (function
                | Reply.OutcomeUnknown operation -> Some operation
                | _ -> None)
                (record id |> optionalField "operationId" id ResponseScalars.operationId)
            variant
                "DIVERGED"
                Reply.Diverged
                (function
                | Reply.Diverged(finding, elapsed) -> Some(finding, elapsed)
                | _ -> None)
                ((record (fun sequence finding elapsed ->
                    {
                        VerificationDivergence.Sequence = sequence
                        Finding = finding
                    },
                    elapsed)
                 : Fields<VerificationDivergence * int64, _>)
                 |> field
                     "sequence"
                     (fst >> _.Sequence)
                     (nullable (
                         integer 0L SafeInteger.Maximum
                         |> map (Sequence.create >> Option.get) Sequence.value
                     ))
                 |> field "finding" (fst >> _.Finding) (enumeration VerificationFinding.all)
                 |> field "elapsedMilliseconds" snd (integer 0L SafeInteger.Maximum))
        ]

    let private discoveryVariants =
        [
            variant
                "USAGE_ERROR"
                Reply.Usage
                (function
                | Reply.Usage usage -> Some usage
                | _ -> None)
                UsageFrame.fields
            variant
                "HELP"
                Reply.Help
                (function
                | Reply.Help help -> Some help
                | _ -> None)
                HelpFrame.fields
            variant
                "VERSION"
                Reply.Version
                (function
                | Reply.Version version -> Some version
                | _ -> None)
                VersionFrame.fields
        ]

    let codec =
        tagged "DatabaseReply" (successVariants @ operationVariants @ discoveryVariants)

    let exitCode =
        function
        | Reply.Operation outcome -> (OutcomePresentation.classify outcome).ExitCode
        | Reply.Usage _
        | Reply.MaintenanceConflict _
        | Reply.MaintenanceRequired -> ExitCode.Rejected
        | Reply.Cancelled -> ExitCode.Failed
        | Reply.OutcomeUnknown _ -> ExitCode.OutcomeUnknown
        | Reply.Diverged _ -> ExitCode.Diverged
        | Reply.Help _
        | Reply.Version _ -> ExitCode.Success

    let encode reply =
        Codec.encode codec reply, ExitCode.value (exitCode reply)

    let initialized id =
        Reply.Operation(Outcome.Succeeded(DatabaseSuccess.Initialized id)) |> encode

    let head position =
        Reply.Operation(Outcome.Succeeded(DatabaseSuccess.Head position)) |> encode

    let status value =
        Reply.Operation(Outcome.Succeeded(DatabaseSuccess.Status value)) |> encode

    let maintenance =
        function
        | Ok value ->
            Reply.Operation(Outcome.Succeeded(DatabaseSuccess.Maintenance value)) |> encode
        | Error actual -> Reply.MaintenanceConflict actual |> encode

    let maintenanceRequired () = Reply.MaintenanceRequired |> encode

    let sessionsPruned value =
        Reply.Operation(Outcome.Succeeded(DatabaseSuccess.SessionsPruned value))
        |> encode

    let rejected refusal =
        Reply.Operation(Outcome.Rejected refusal) |> encode

    let failed fault =
        Reply.Operation(Outcome.Failed fault) |> encode

    let unknown operation =
        Reply.OutcomeUnknown operation |> encode

    let private accepted make (receipt: OperationReceipt) =
        match receipt.Subject with
        | ReceiptSubject.Authority target ->
            {
                OperationId = receipt.OperationId
                TargetActorId = target.TargetActorId
                Position = receipt.Position
            }
            |> make
            |> Outcome.Succeeded
            |> Reply.Operation
            |> encode
        | ReceiptSubject.Case _ -> failed CoreFault.InternalError

    let authority =
        function
        | ProvisionOwnerOutcome.Accepted receipt -> accepted DatabaseSuccess.Accepted receipt
        | ProvisionOwnerOutcome.AlreadyAccepted receipt ->
            accepted DatabaseSuccess.AlreadyAccepted receipt
        | ProvisionOwnerOutcome.Rejected refusal -> rejected refusal
        | ProvisionOwnerOutcome.Failed fault -> failed fault
        | ProvisionOwnerOutcome.Cancelled -> encode Reply.Cancelled
        | ProvisionOwnerOutcome.OutcomeUnknown operation -> unknown (Some operation)

    let verification elapsed =
        function
        | VerificationResult.Verified position ->
            encode (Reply.Operation(Outcome.Succeeded(DatabaseSuccess.Verified(position, elapsed))))
        | VerificationResult.Diverged finding -> encode (Reply.Diverged(finding, elapsed))

    let rebuild =
        function
        | Ok report -> encode (Reply.Operation(Outcome.Succeeded(DatabaseSuccess.Rebuilt report)))
        | Error finding -> encode (Reply.Diverged(finding, 0L))

    let sessionsInvalidated count =
        encode (
            Reply.Operation(Outcome.Succeeded(DatabaseSuccess.RestoredSessionsInvalidated count))
        )

    let usage error = encode (Reply.Usage error)

    let help () =
        Reply.Help
            {
                Acts = CommandName.all |> List.map CommandName.token
                ExitCodes =
                    Command.supportedExitCodes
                    |> List.map (fun code -> ExitCode.token code, ExitCode.value code)
            }
        |> encode

    let version () =
        let identity = BuildIdentity.current

        Reply.Version
            {
                Product = identity.Product
                Version = identity.Version
                AssemblyVersion = identity.AssemblyVersion
                FileVersion = identity.FileVersion
            }
        |> encode

    let write (output: IO.Stream) (bytes: byte array, code, deliveryUnknown) =
        try
            output.Write bytes
            output.WriteByte(byte '\n')
            output.Flush()
            code
        with _ ->
            ExitCode.value (
                if deliveryUnknown then
                    ExitCode.OutcomeUnknown
                else
                    ExitCode.Failed
            )
