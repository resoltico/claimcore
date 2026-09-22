namespace ClaimCore.Cli

open System
open ClaimCore.Application
open ClaimCore.Contracts

/// A returned native outcome is retained before presentation can fail. No encoded JSON is parsed
/// to recover operation knowledge, and no display sentence selects an outcome.
[<RequireQualifiedAccess; NoEquality; NoComparison>]
type EndpointReply =
    | Protocol of exitCode: int * ProtocolFailure
    | Local of Endpoint * CliLocalFault
    | Get of QueryOutcome<Lookup<CurrentCase, string>>
    | List of QueryOutcome<CaseSummaryPage>
    | History of QueryOutcome<Lookup<HistoryResultPage, string>>
    | Observe of QueryOutcome<Lookup<OperationReceipt, Guid>>
    | RecoveryList of RecoveryQueryOutcome<RecoveryPage>
    | Inspect of RecoveryQueryOutcome<Lookup<RecoveryInspection, Guid>>
    | Resolve of ResolveOutcome
    | Dismiss of RecoveryDismissOutcome
    | Export of Guid * RecoveryQueryOutcome<Lookup<RecoveryExport, Guid>>
    | Exported of Guid * digest: string * mediaType: string
    | Prepare of PrepareOutcome
    | Submit of SubmissionOutcome
    | EnvelopePreview of RecoveryQueryOutcome<RecoveryImportPreview>
    | RecordPreview of RecoveryQueryOutcome<RecoveryImportPreview>
    | EnvelopeRetain of RecoveryImportRetainOutcome
    | RecordRetain of RecoveryImportRetainOutcome

module EndpointReply =
    let private recovery reply =
        let name = Endpoint.identifier

        match reply with
        | EndpointReply.RecoveryList value ->
            CliWireCodec.recoveryList (name Endpoint.RecoveryList) value
        | EndpointReply.Inspect value ->
            CliWireCodec.recoveryInspect (name Endpoint.RecoveryInspect) value
        | EndpointReply.Resolve value -> CliWireCodec.resolve (name Endpoint.RecoveryResolve) value
        | EndpointReply.Dismiss value -> CliWireCodec.dismiss (name Endpoint.RecoveryDismiss) value
        | EndpointReply.Export(id, value) ->
            CliWireCodec.recoveryExport (name Endpoint.RecoveryExport) id value
        | EndpointReply.Exported(id, digest, media) ->
            CliWireCodec.exported (name Endpoint.RecoveryExport) id digest media
        | EndpointReply.EnvelopePreview value ->
            CliWireCodec.importPreview (name Endpoint.RecoveryImportEnvelopePreview) value
        | EndpointReply.RecordPreview value ->
            CliWireCodec.importPreview (name Endpoint.RecoveryImportRecordPreview) value
        | EndpointReply.EnvelopeRetain value ->
            CliWireCodec.importRetain (name Endpoint.RecoveryImportEnvelopeRetain) value
        | EndpointReply.RecordRetain value ->
            CliWireCodec.importRetain (name Endpoint.RecoveryImportRecordRetain) value
        | _ -> invalidArg (nameof reply) "Expected a recovery reply."

    let encode reply =
        let name = Endpoint.identifier

        match reply with
        | EndpointReply.Protocol(code, failure) -> CliWireCodec.protocolFailure code failure
        | EndpointReply.Local(endpoint, failure) ->
            CliWireCodec.localFailure (name endpoint) failure
        | EndpointReply.Get value -> CliWireCodec.caseGet (name Endpoint.CaseGet) value
        | EndpointReply.List value -> CliWireCodec.caseList (name Endpoint.CaseList) value
        | EndpointReply.History value -> CliWireCodec.caseHistory (name Endpoint.CaseHistory) value
        | EndpointReply.Observe value ->
            CliWireCodec.operationObserve (name Endpoint.OperationObserve) value
        | EndpointReply.Prepare value -> CliWireCodec.prepare (name Endpoint.CommandPrepare) value
        | EndpointReply.Submit value -> CliWireCodec.submission (name Endpoint.CommandExecute) value
        | value -> recovery value
