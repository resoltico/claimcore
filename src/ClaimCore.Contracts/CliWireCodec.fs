namespace ClaimCore.Contracts

open System
open System.Buffers
open System.Text.Json
open ClaimCore.Application

module CliWireCodec =
    let private encode (exitCode: int) (write: Utf8JsonWriter -> unit) =
        let buffer = ArrayBufferWriter<byte>()

        use writer =
            new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = false, SkipValidation = false))

        write writer
        writer.Flush()

        {
            ExitCode = exitCode
            Bytes = Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]
        }

    let private result (endpoint: string) (exitCode: int) (writeOutcome: Utf8JsonWriter -> unit) =
        encode exitCode (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("protocolVersion", 3)
            writer.WriteString("kind", "result")
            writer.WriteString("endpoint", endpoint)
            writer.WritePropertyName("outcome")
            writeOutcome writer
            writer.WriteEndObject())

    let protocolFailure (exitCode: int) (failure: ProtocolFailure) =
        encode exitCode (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("protocolVersion", 3)
            writer.WriteString("kind", "protocolFailure")
            writer.WriteString("code", failure.Code)
            writer.WritePropertyName("diagnostic")
            writer.WriteStartObject()
            writer.WriteString("id", ProtocolProblems.token failure.Reason)
            writer.WritePropertyName("parameters")
            writer.WriteStartObject()
            writer.WriteEndObject()
            writer.WriteEndObject()
            writer.WriteString("message", ProtocolProblems.render failure.Reason)
            writer.WriteString("path", failure.Path)
            writer.WriteEndObject())

    let private queryExit (outcome: QueryOutcome<Lookup<'value, 'identity>>) =
        match outcome with
        | QueryOutcome.Succeeded(Lookup.Found _) -> 0
        | QueryOutcome.Succeeded(Lookup.NotFound _)
        | QueryOutcome.Rejected _ -> 2
        | QueryOutcome.Failed _
        | QueryOutcome.Cancelled -> 3

    let private pageQueryExit (outcome: QueryOutcome<'value>) =
        match outcome with
        | QueryOutcome.Succeeded _ -> 0
        | QueryOutcome.Rejected _ -> 2
        | QueryOutcome.Failed _
        | QueryOutcome.Cancelled -> 3

    let private recoveryQueryExit (outcome: RecoveryQueryOutcome<'value>) =
        match outcome with
        | RecoveryQueryOutcome.RecoverySucceeded _ -> 0
        | RecoveryQueryOutcome.RecoveryRejected _ -> 2
        | RecoveryQueryOutcome.RecoveryFailed _
        | RecoveryQueryOutcome.RecoveryCancelled -> 3

    let private recoveryLookupExit (outcome: RecoveryQueryOutcome<Lookup<'value, 'identity>>) =
        match outcome with
        | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found _) -> 0
        | RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound _)
        | RecoveryQueryOutcome.RecoveryRejected _ -> 2
        | RecoveryQueryOutcome.RecoveryFailed _
        | RecoveryQueryOutcome.RecoveryCancelled -> 3

    let private executionExit (execution: DefiniteExecution) =
        match execution with
        | DefiniteExecution.Accepted _ -> 0
        | DefiniteExecution.ExecutionRejected _ -> 2
        | DefiniteExecution.ExecutionRevokedBeforeExecution _ -> 2
        | DefiniteExecution.FailedBeforeCommit _ -> 3

    let private submissionExit (outcome: SubmissionOutcome) =
        match outcome with
        | SubmissionOutcome.ObservedAccepted _ -> 0
        | SubmissionOutcome.Completed(_, _, execution, SettlementConfirmation.Confirmed) ->
            executionExit execution
        | SubmissionOutcome.Completed _
        | SubmissionOutcome.PreparationStateUnknown _
        | SubmissionOutcome.AttemptAdmissionUnknown _
        | SubmissionOutcome.AttemptUnresolved _ -> 4
        | SubmissionOutcome.RejectedBeforeAttempt _ -> 2
        | SubmissionOutcome.FailedBeforeAttempt _ -> 3
        | SubmissionOutcome.CancelledBeforeAdmission _
        | SubmissionOutcome.CancelledBeforeAttempt _ -> 130

    let private resolveExit (outcome: ResolveOutcome) =
        match outcome with
        | ResolveOutcome.ResolveObservedAccepted _ -> 0
        | ResolveOutcome.ResolveCompleted(_, _, execution, SettlementConfirmation.Confirmed) ->
            executionExit execution
        | ResolveOutcome.ResolveCompleted _
        | ResolveOutcome.ResolveAttemptAdmissionUnknown _
        | ResolveOutcome.ResolveAttemptUnresolved _ -> 4
        | ResolveOutcome.RefusedBeforeAttempt _ -> 2
        | ResolveOutcome.ResolveFailedBeforeAttempt _ -> 3
        | ResolveOutcome.ResolveCancelledBeforeAdmission _
        | ResolveOutcome.ResolveCancelledBeforeAttempt _ -> 130

    let private dismissExit (outcome: RecoveryDismissOutcome) =
        match outcome with
        | RecoveryDismissOutcome.DismissedPreparation _
        | RecoveryDismissOutcome.AlreadyDismissedPreparation _
        | RecoveryDismissOutcome.AlreadyRevoked _ -> 0
        | RecoveryDismissOutcome.DismissNotFound _
        | RecoveryDismissOutcome.DismissRefused _ -> 2
        | RecoveryDismissOutcome.DismissFailed _ -> 3
        | RecoveryDismissOutcome.DismissCancelledBeforeAdmission _ -> 130
        | RecoveryDismissOutcome.DismissStateUnknown _ -> 4

    let private retainExit (outcome: RecoveryImportRetainOutcome) =
        match outcome with
        | RecoveryImportRetainOutcome.RetainedPreparation _
        | RecoveryImportRetainOutcome.ExistingPreparation _
        | RecoveryImportRetainOutcome.ObservedAcceptedImport _ -> 0
        | RecoveryImportRetainOutcome.ImportRejected _ -> 2
        | RecoveryImportRetainOutcome.ImportFailed _ -> 3
        | RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission -> 130
        | RecoveryImportRetainOutcome.RetainStateUnknown _ -> 4

    let private prepareExit (outcome: PrepareOutcome) =
        match outcome with
        | PrepareOutcome.Prepared _
        | PrepareOutcome.ObservedAccepted _ -> 0
        | PrepareOutcome.RetainedForRecovery _ -> 2
        | PrepareOutcome.PrepareRejected _ -> 2
        | PrepareOutcome.PrepareFailed _ -> 3
        | PrepareOutcome.CancelledBeforeAdmission _ -> 130
        | PrepareOutcome.PreparationStateUnknown _ -> 4

    let localFailure (endpoint: string) (fault: CliLocalFault) =
        result endpoint 3 (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("kind", "localFailure")
            writer.WritePropertyName("fault")
            CliWireValues.localFault writer fault
            writer.WriteEndObject())

    let caseGet (endpoint: string) (outcome: QueryOutcome<Lookup<CurrentCase, string>>) =
        result endpoint (queryExit outcome) (fun writer ->
            CliWireQueries.currentCase writer outcome)

    let caseList (endpoint: string) (outcome: QueryOutcome<CaseSummaryPage>) =
        result endpoint (pageQueryExit outcome) (fun writer ->
            CliWireQueries.casePage writer outcome)

    let caseHistory (endpoint: string) (outcome: QueryOutcome<Lookup<HistoryResultPage, string>>) =
        result endpoint (queryExit outcome) (fun writer -> CliWireQueries.history writer outcome)

    let operationObserve
        (endpoint: string)
        (outcome: QueryOutcome<Lookup<OperationReceipt, Guid>>)
        =
        result endpoint (queryExit outcome) (fun writer -> CliWireQueries.operation writer outcome)

    let recoveryList (endpoint: string) (outcome: RecoveryQueryOutcome<RecoveryPage>) =
        result endpoint (recoveryQueryExit outcome) (fun writer ->
            CliWireQueries.recoveryPage writer outcome)

    let recoveryInspect
        (endpoint: string)
        (outcome: RecoveryQueryOutcome<Lookup<RecoveryInspection, Guid>>)
        =
        result endpoint (recoveryLookupExit outcome) (fun writer ->
            CliWireQueries.recoveryDetails writer outcome)

    let recoveryExport
        (endpoint: string)
        (operationId: Guid)
        (outcome: RecoveryQueryOutcome<Lookup<RecoveryExport, Guid>>)
        =
        result endpoint (recoveryLookupExit outcome) (fun writer ->
            CliWireQueries.recoveryExport operationId writer outcome)

    let prepare (endpoint: string) (outcome: PrepareOutcome) =
        result endpoint (prepareExit outcome) (fun writer ->
            CliWirePreparation.prepare writer outcome)

    let submission (endpoint: string) (outcome: SubmissionOutcome) =
        result endpoint (submissionExit outcome) (fun writer ->
            CliWireMutations.submission writer outcome)

    let resolve (endpoint: string) (outcome: ResolveOutcome) =
        result endpoint (resolveExit outcome) (fun writer ->
            CliWireMutations.resolve writer outcome)

    let dismiss (endpoint: string) (outcome: RecoveryDismissOutcome) =
        result endpoint (dismissExit outcome) (fun writer ->
            CliWireMutations.dismiss writer outcome)

    let importPreview (endpoint: string) (outcome: RecoveryQueryOutcome<RecoveryImportPreview>) =
        result endpoint (recoveryQueryExit outcome) (fun writer ->
            CliWireImports.preview writer outcome)

    let importRetain (endpoint: string) (outcome: RecoveryImportRetainOutcome) =
        result endpoint (retainExit outcome) (fun writer -> CliWireImports.retain writer outcome)

    let exported (endpoint: string) (operationId: Guid) (digest: string) (mediaType: string) =
        result endpoint 0 (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("kind", "exported")
            writer.WriteString("operationId", operationId)
            writer.WriteString("requestSha256", digest)
            writer.WriteString("mediaType", mediaType)
            writer.WriteEndObject())
