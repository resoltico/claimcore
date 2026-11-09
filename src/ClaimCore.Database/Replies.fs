namespace ClaimCore.Database

open System
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.RecordFormat

[<RequireQualifiedAccess; NoComparison>]
type DatabaseSuccess =
    | Initialized of Guid
    | Rebuilt of ProjectionRebuildReport
    | RestoredInstallationAdmitted of RestoreAdmissionReport
    | Status of InstallationStatus
    | Maintenance of MaintenanceObservation
    | Verified of LedgerPosition * elapsedMilliseconds: int64

[<RequireQualifiedAccess; NoComparison>]
type Reply =
    | Succeeded of DatabaseSuccess
    | OutcomeUnknown of OperationId option
    | Diverged of VerificationDivergence * elapsedMilliseconds: int64
    | Usage of UsageFrame
    | Help of HelpFrame
    | Version of VersionFrame
    | Fault of OperatorFault

/// Administrative successes share failures with other public outcomes; uncertainty remains distinct.
module Replies =
    open Codec

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
                installationFormat
                owner
                actors
                cases
                sessions
                expired
                work
                reminders ->
                {
                    InstallationStatus.InstallationId = installation
                    Baseline = baseline
                    BaselineSha256 = digest
                    BusinessTimeZone = zone
                    ObservedAt = instant
                    Position = position
                    Maintenance = maintenance
                    InstallationFormat = installationFormat
                    InitialOwner = owner
                    Actors = actors
                    Cases = cases
                    Sessions = sessions
                    ExpiredSessions = expired
                    WorkRecords = work
                    Reminders = reminders
                })
        |> field "installationId" (fun (value: InstallationStatus) -> value.InstallationId) uuid
        |> field "baseline" _.Baseline (boundedText 1 64)
        |> field "baselineSha256" _.BaselineSha256 ResponseScalars.digest
        |> field "businessTimeZone" _.BusinessTimeZone (boundedText 1 128)
        |> field "observedAt" _.ObservedAt instant
        |> field "position" _.Position administrativePosition
        |> field "maintenance" _.Maintenance ResponseCodecs.registerMaintenance
        |> field "installationFormat" _.InstallationFormat (integer 1L 32767L |> map int int64)
        |> field "initialOwner" _.InitialOwner (nullable ResponseCodecs.actorRef)
        |> field "actors" _.Actors countCodec
        |> field "cases" _.Cases countCodec
        |> field "sessions" _.Sessions countCodec
        |> field "expiredSessions" _.ExpiredSessions countCodec
        |> field "workRecords" _.WorkRecords countCodec
        |> field "reminders" _.Reminders countCodec

    // This closed codec table pairs every success constructor with its inverse projection; it has no nested decisions.
    let private successVariants =
        [
            variant
                "STATUS"
                (DatabaseSuccess.Status >> Reply.Succeeded)
                (function
                | Reply.Succeeded(DatabaseSuccess.Status value) -> Some value
                | _ -> None)
                statusFields
            variant
                "MAINTENANCE"
                (DatabaseSuccess.Maintenance >> Reply.Succeeded)
                (function
                | Reply.Succeeded(DatabaseSuccess.Maintenance value) -> Some value
                | _ -> None)
                maintenanceFields
            variant
                "REBUILT"
                (DatabaseSuccess.Rebuilt >> Reply.Succeeded)
                (function
                | Reply.Succeeded(DatabaseSuccess.Rebuilt report) -> Some report
                | _ -> None)
                rebuildFields
            variant
                "RESTORED_INSTALLATION_ADMITTED"
                (DatabaseSuccess.RestoredInstallationAdmitted >> Reply.Succeeded)
                (function
                | Reply.Succeeded(DatabaseSuccess.RestoredInstallationAdmitted report) ->
                    Some report
                | _ -> None)
                (record (fun checkpoint count ->
                    {
                        RestoreAdmissionReport.Checkpoint = checkpoint
                        InvalidatedSessions = count
                    })
                 |> field "checkpoint" _.Checkpoint CheckpointRecord.codec
                 |> field
                     "invalidatedSessions"
                     _.InvalidatedSessions
                     (integer 0L SafeInteger.Maximum))
            variant
                "INITIALIZED"
                (DatabaseSuccess.Initialized >> Reply.Succeeded)
                (function
                | Reply.Succeeded(DatabaseSuccess.Initialized id) -> Some id
                | _ -> None)
                (record id |> field "installationId" id uuid)
            variant
                "VERIFIED"
                (DatabaseSuccess.Verified >> Reply.Succeeded)
                (function
                | Reply.Succeeded(DatabaseSuccess.Verified(position, elapsed)) ->
                    Some(position, elapsed)
                | _ -> None)
                (record (fun position elapsed -> position, elapsed)
                 |> field "position" fst administrativePosition
                 |> field "elapsedMilliseconds" snd (integer 0L SafeInteger.Maximum))
        ]

    let private operationVariants =
        [
            variant
                "OPERATOR_FAULT"
                Reply.Fault
                (function
                | Reply.Fault fault -> Some fault
                | _ -> None)
                (record id |> field "fault" id (enumeration OperatorFault.all))
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
        | Reply.Succeeded _ -> ExitCode.Success
        | Reply.Fault(OperatorFault.Unavailable | OperatorFault.IntegrityFailure | OperatorFault.InternalFailure | OperatorFault.Cancelled) ->
            ExitCode.Failed
        | Reply.Fault _
        | Reply.Usage _ -> ExitCode.Rejected
        | Reply.OutcomeUnknown _ -> ExitCode.OutcomeUnknown
        | Reply.Diverged _ -> ExitCode.Diverged
        | Reply.Help _
        | Reply.Version _ -> ExitCode.Success

    let encode reply =
        Codec.encode codec reply, ExitCode.value (exitCode reply)

    let initialized id =
        Reply.Succeeded(DatabaseSuccess.Initialized id) |> encode

    /// Head is the canonical checkpoint artifact, directly reusable as --checkpoint FILE.
    let head checkpoint =
        CheckpointRecord.encode checkpoint, ExitCode.value ExitCode.Success

    let status value =
        Reply.Succeeded(DatabaseSuccess.Status value) |> encode

    let maintenance value =
        Reply.Succeeded(DatabaseSuccess.Maintenance value) |> encode

    let operatorFault fault = encode (Reply.Fault fault)

    let unknown operation =
        Reply.OutcomeUnknown operation |> encode

    let verification elapsed =
        function
        | VerificationResult.Verified position ->
            encode (Reply.Succeeded(DatabaseSuccess.Verified(position, elapsed)))
        | VerificationResult.Diverged finding -> encode (Reply.Diverged(finding, elapsed))

    let rebuild =
        function
        | Ok report -> encode (Reply.Succeeded(DatabaseSuccess.Rebuilt report))
        | Error finding -> encode (Reply.Diverged(finding, 0L))

    let restoreAdmission =
        function
        | Ok report -> encode (Reply.Succeeded(DatabaseSuccess.RestoredInstallationAdmitted report))
        | Error finding -> encode (Reply.Diverged(finding, 0L))

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
