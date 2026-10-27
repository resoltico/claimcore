namespace ClaimCore.Database

open System
open ClaimCore.Contract
open ClaimCore.Contract.Protocol
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.Application
open ClaimCore.Postgres

type OwnerAcceptance =
    {
        OperationId: Guid
        TargetActorId: Guid
        Position: LedgerPosition
    }

[<RequireQualifiedAccess; NoComparison>]
type Reply =
    | Initialized of Guid
    | Head of LedgerPosition
    | Accepted of OwnerAcceptance
    | AlreadyAccepted of OwnerAcceptance
    | Rejected of Refusal
    | Failed of CoreFault
    | Cancelled
    | OutcomeUnknown of Guid option
    | Verified of LedgerPosition
    | Diverged of VerificationDivergence
    | Usage of UsageIssue * string list
    | Help of string list * (string * int) list
    | Version of string * string * string * string

/// Database replies have a local codec; they do not add a public contract document.
module Replies =
    open Codec

    let private acceptance =
        record (fun operation target position ->
            {
                OperationId = operation
                TargetActorId = target
                Position = position
            })
        |> field "operationId" _.OperationId uuid
        |> field "targetActorId" _.TargetActorId uuid
        |> field "position" _.Position ResponseCodecs.position

    let private exitEntry =
        record (fun code number -> code, number)
        |> field "code" fst text
        |> field "number" snd (integer 0L 255L |> map int int64)
        |> build

    let private usageIssue =
        Vocabulary.ofUnion<UsageIssue> "usage" TokenCasing.ScreamingSnake None
        |> VocabularyCodec.create

    let private successVariants =
        [
            variant
                "INITIALIZED"
                Reply.Initialized
                (function
                | Reply.Initialized id -> Some id
                | _ -> None)
                (record id |> field "installationId" id uuid)
            variant
                "HEAD"
                Reply.Head
                (function
                | Reply.Head position -> Some position
                | _ -> None)
                (record id |> field "position" id ResponseCodecs.position)
            variant
                "ACCEPTED"
                Reply.Accepted
                (function
                | Reply.Accepted receipt -> Some receipt
                | _ -> None)
                acceptance
            variant
                "ALREADY_ACCEPTED"
                Reply.AlreadyAccepted
                (function
                | Reply.AlreadyAccepted receipt -> Some receipt
                | _ -> None)
                acceptance
        ]

    let private outcomeVariants =
        [
            variant
                "REJECTED"
                Reply.Rejected
                (function
                | Reply.Rejected diagnostic -> Some diagnostic
                | _ -> None)
                (record id |> field "diagnostic" id DiagnosticCodecs.diagnostic)
            variant
                "FAILED"
                Reply.Failed
                (function
                | Reply.Failed fault -> Some fault
                | _ -> None)
                (record id |> field "fault" id DiagnosticCodecs.fault)
            unitVariant "CANCELLED" Reply.Cancelled
            variant
                "OUTCOME_UNKNOWN"
                Reply.OutcomeUnknown
                (function
                | Reply.OutcomeUnknown operation -> Some operation
                | _ -> None)
                (record id |> optionalField "operationId" id uuid)
            variant
                "VERIFIED"
                Reply.Verified
                (function
                | Reply.Verified position -> Some position
                | _ -> None)
                (record id |> field "position" id ResponseCodecs.position)
            variant
                "DIVERGED"
                Reply.Diverged
                (function
                | Reply.Diverged finding -> Some finding
                | _ -> None)
                (record (fun sequence finding ->
                    {
                        VerificationDivergence.Sequence = sequence
                        Finding = finding
                    })
                 |> field
                     "sequence"
                     (fun (value: VerificationDivergence) -> value.Sequence)
                     (nullable (integer 0L SafeInteger.Maximum))
                 |> field
                     "finding"
                     (fun (value: VerificationDivergence) -> value.Finding)
                     (enumeration VerificationFindings.all))
            variant
                "USAGE_ERROR"
                Reply.Usage
                (function
                | Reply.Usage(issue, usage) -> Some(issue, usage)
                | _ -> None)
                (record (fun issue usage -> issue, usage)
                 |> field "issue" fst usageIssue
                 |> field "usage" snd (list Int32.MaxValue text))
        ]

    let private discoveryVariants =
        [
            variant
                "HELP"
                Reply.Help
                (function
                | Reply.Help(acts, codes) -> Some(acts, codes)
                | _ -> None)
                (record (fun acts codes -> acts, codes)
                 |> field "acts" fst (list Int32.MaxValue text)
                 |> field "exitCodes" snd (list Int32.MaxValue exitEntry))
            variant
                "VERSION"
                Reply.Version
                (function
                | Reply.Version(product, version, assembly, file) ->
                    Some(product, version, assembly, file)
                | _ -> None)
                (record (fun product version assembly file -> product, version, assembly, file)
                 |> field "product" (fun (product, _, _, _) -> product) text
                 |> field "version" (fun (_, version, _, _) -> version) text
                 |> field "assemblyVersion" (fun (_, _, assembly, _) -> assembly) text
                 |> field "fileVersion" (fun (_, _, _, file) -> file) text)
        ]

    let codec =
        tagged "DatabaseReply" (successVariants @ outcomeVariants @ discoveryVariants)

    // Exhaustive outcome policy, including delivery uncertainty and administrative divergence.
    // fsharplint:disable-next-line CyclomaticComplexity
    let private exit =
        function
        | Reply.Rejected _
        | Reply.Usage _ -> ExitCode.Rejected
        | Reply.Failed _
        | Reply.Cancelled -> ExitCode.Failed
        | Reply.OutcomeUnknown _ -> ExitCode.OutcomeUnknown
        | Reply.Diverged _ -> ExitCode.Diverged
        | Reply.Initialized _
        | Reply.Head _
        | Reply.Accepted _
        | Reply.AlreadyAccepted _
        | Reply.Verified _
        | Reply.Help _
        | Reply.Version _ -> ExitCode.Success

    let encode reply =
        Codec.encode codec reply, ExitCodes.value (exit reply)

    let initialized id = Reply.Initialized id |> encode
    let head position = Reply.Head position |> encode
    let rejected diagnostic = Reply.Rejected diagnostic |> encode
    let failed fault = Reply.Failed fault |> encode

    let unknown operation =
        Reply.OutcomeUnknown operation |> encode

    let private accepted make (receipt: OperationReceipt) =
        match receipt.Subject with
        | ReceiptSubject.Authority target ->
            make
                {
                    OperationId = receipt.OperationId
                    TargetActorId = target.TargetActorId
                    Position = receipt.Position
                }
            |> encode
        | ReceiptSubject.Case _ -> failed CoreFault.InternalError

    let authority =
        function
        | ProvisionOwnerOutcome.Accepted receipt -> accepted Reply.Accepted receipt
        | ProvisionOwnerOutcome.AlreadyAccepted receipt -> accepted Reply.AlreadyAccepted receipt
        | ProvisionOwnerOutcome.Rejected refusal -> rejected refusal
        | ProvisionOwnerOutcome.Failed fault -> failed fault
        | ProvisionOwnerOutcome.Cancelled -> encode Reply.Cancelled
        | ProvisionOwnerOutcome.OutcomeUnknown operation -> unknown (Some operation)

    let verification =
        function
        | VerificationResult.Verified position -> encode (Reply.Verified position)
        | VerificationResult.Diverged finding -> encode (Reply.Diverged finding)

    let usage (error: UsageError) =
        let usage =
            error.Command
            |> Option.map (Commands.synopsis >> List.singleton)
            |> Option.defaultWith (fun () -> Commands.all |> List.map Commands.synopsis)

        encode (Reply.Usage(error.Issue, usage))

    let help () =
        encode (
            Reply.Help(
                Commands.all |> List.map Commands.token,
                ExitCodes.all
                |> List.filter ExitCodes.database
                |> List.map (fun code -> ExitCodes.token code, ExitCodes.value code)
            )
        )

    let version () =
        let identity = BuildIdentity.current

        encode (
            Reply.Version(
                identity.Product,
                identity.Version,
                identity.AssemblyVersion,
                identity.FileVersion
            )
        )

    let write (output: IO.Stream) (bytes: byte array, code, deliveryUnknown) =
        try
            output.Write bytes
            output.WriteByte(byte '\n')
            output.Flush()
            code
        with _ ->
            ExitCodes.value (
                if deliveryUnknown then
                    ExitCode.OutcomeUnknown
                else
                    ExitCode.Failed
            )
