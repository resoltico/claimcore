namespace ClaimCore.ContractGeneration

open System
open System.Buffers
open System.Text.Json
open ClaimCore.Contracts
open ClaimCore.Database
open ClaimCore.Postgres

module internal ProcessDiagnosticCorpus =
    let private parsed (bytes: byte array) =
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
        document.RootElement.Clone()

    let private replace name write value =
        CorpusJson.rewrite value name (Some write) false

    let private samples surface id bytes =
        let value = parsed bytes
        let diagnostic = value.GetProperty("diagnostic")

        let withDiagnostic (transform: JsonElement -> JsonElement) =
            replace "diagnostic" (fun writer -> (transform diagnostic).WriteTo writer) value

        let wrongId =
            withDiagnostic (replace "id" (fun writer -> writer.WriteStringValue("UNDECLARED")))

        let extra =
            withDiagnostic (
                replace "parameters" (fun writer ->
                    writer.WriteStartObject()
                    writer.WriteString("private", "DO-NOT-ADMIT")
                    writer.WriteEndObject())
            )

        [
            id, surface, true, value
            id + "-translated",
            surface,
            true,
            replace "message" (fun writer -> writer.WriteStringValue("Atbilde — العربية")) value
            id + "-unknown-id", surface, false, wrongId
            id + "-extra-parameters", surface, false, extra
            id + "-missing-diagnostic",
            surface,
            false,
            CorpusJson.rewrite value "diagnostic" None false
            id + "-extra-root", surface, false, CorpusJson.rewrite value "" None true
        ]

    let private cli =
        let id = Guid.Parse("90000000-0000-4000-8000-000000000001")

        [
            CliProcessProblem.UnsupportedInvocation, CliDeliveryPhase.Idle, false, None
            CliProcessProblem.UnexpectedFailure, CliDeliveryPhase.Idle, false, None
            CliProcessProblem.InputReadFailed, CliDeliveryPhase.Reading, false, None
            CliProcessProblem.ServiceAcquireFailed, CliDeliveryPhase.AcquiringService, false, None
            CliProcessProblem.DispatchFailed,
            CliDeliveryPhase.Dispatching,
            true,
            Some(id, Some(String.replicate 64 "a"))
            CliProcessProblem.SerializationFailed,
            CliDeliveryPhase.ResultObserved,
            true,
            Some(id, None)
            CliProcessProblem.OutputWriteFailed,
            CliDeliveryPhase.ResultAvailable 0,
            true,
            Some(id, None)
        ]
        |> List.collect (fun (reason, phase, changed, context) ->
            CliProcessDiagnostics.encode reason phase changed context
            |> samples "cliProcess" (CliProcessDiagnostics.token reason))

    let private web =
        WebStartupDiagnostics.all
        |> List.mapi (fun index reason ->
            WebStartupDiagnostics.encode reason
            |> samples "webProcess" (WebStartupDiagnostics.token reason + "-" + string index))
        |> List.concat

    let private adminFailures =
        DatabaseDiagnostics.nativeReasons
        |> List.filter ((<>) AdministrationFailure.DataAuditFailed)
        |> List.collect (fun reason ->
            let outcomes: AdministrationOutcome<PreparationPruneResult option> list =
                if reason = AdministrationFailure.CommitUnconfirmed then
                    [ AdministrationOutcome.CompletionUnknown reason ]
                else
                    [
                        AdministrationOutcome.NotStarted reason
                        AdministrationOutcome.NotCommitted reason
                    ]

            outcomes
            |> List.collect (fun outcome ->
                DatabaseDiagnostics.outcome DatabaseCommand.Verify outcome
                |> samples
                    "administration"
                    (DatabaseDiagnostics.nativeToken reason
                     + "-"
                     + DatabaseDiagnostics.outcomeToken outcome)))

    let private adminInputs =
        DatabaseDiagnostics.inputReasons @ [ DatabaseInputProblem.ProcessFailed ]
        |> List.mapi (fun index reason ->
            DatabaseDiagnostics.inputFailure reason
            |> samples
                "administration"
                (DatabaseDiagnostics.inputToken reason + "-" + string index))
        |> List.concat

    let private adminCompletion =
        let counts =
            {
                CandidateCount = 1
                DeletedCount = 0
                DryRun = true
                TerminalPreparationCount = 9007199254740993L
                TerminalCanonicalRequestBytes = Int64.MaxValue
            }

        [
            DatabaseCommand.Verify, None
            DatabaseCommand.Initialize "Etc/UTC", None
            DatabaseCommand.Prune PreparationPruneOptions.defaults, Some counts
        ]
        |> List.collect (fun (command, data) ->
            let id = "admin-completion-" + DatabaseArguments.commandToken command
            let result = AdministrationOutcome.Completed data
            let success = DatabaseDiagnostics.outcome command result |> parsed

            [
                id, "administration", true, success
                id + "-extra", "administration", false, CorpusJson.rewrite success "" None true
            ]
            @ (DatabaseDiagnostics.outcome
                command
                (AdministrationOutcome.CompletedCleanupFailed data)
               |> samples "administration" (id + "-cleanup"))
            @ (DatabaseDiagnostics.deliveryFailure command result
               |> samples "administration" (id + "-delivery")))

    let private dataAuditCounts =
        {|
            cases = "1"
            acceptedOperations = "1"
            lifecycleEvents = "0"
            erasureFences = "0"
            terminalApprovals = "0"
            terminalEvents = "0"
            revocations = "0"
            authorityEvents = "1"
            actors = "1"
            grants = "1"
            signerApprovals = "0"
            copyDeletionApprovals = "0"
            writerHandoffApprovals = "0"
            writerHandoffPreparations = "0"
            writerHandoffs = "0"
            writerActivations = "0"
            writerHandoffAborts = "0"
            signerKeys = "0"
            signerEvents = "0"
            ownerManagedCopies = "0"
            copyPhysicalVerifications = "0"
            managedExports = "0"
            witnessEntries = "2"
            pendingIntents = "0"
        |}

    let private adminDataAudit =
        let verified =
            JsonSerializer.SerializeToElement(
                {|
                    kind = "dataAuditResult"
                    command = "VERIFY_DATA"
                    scope = "CURRENT_PRIMARY_AND_WITNESS"
                    status = "VERIFIED"
                    installationId = "10000000-0000-4000-8000-000000000001"
                    lineageId = "10000000-0000-4000-8000-000000000002"
                    epoch = "1"
                    witnessCutoff = "2"
                    witnessTipHash = String.replicate 64 "a"
                    verifiedCaseTipsSha256 = String.replicate 64 "b"
                    counts = dataAuditCounts
                |}
            )

        let quarantined =
            JsonSerializer.SerializeToElement(
                {|
                    kind = "dataAuditResult"
                    command = "VERIFY_DATA"
                    status = "QUARANTINED"
                    diagnostic =
                        {|
                            id = "DB_DATA_AUDIT_FAILED"
                            parameters = Map.ofList [ "category", "EVIDENCE_DIVERGENCE" ]
                        |}
                    recommendedAction = "INSPECT_AND_RECONCILE"
                |}
            )

        [
            "admin-verify-data", "administration", true, verified
            "admin-verify-data-numeric-cutoff",
            "administration",
            false,
            replace "witnessCutoff" (fun writer -> writer.WriteNumberValue(2)) verified
            "admin-verify-data-extra",
            "administration",
            false,
            CorpusJson.rewrite verified "" None true
            "admin-verify-data-quarantined", "administration", true, quarantined
            "admin-verify-data-forged",
            "administration",
            false,
            replace "status" (fun writer -> writer.WriteStringValue("DELETED")) quarantined
        ]

    let private writeCase (writer: Utf8JsonWriter) (id, surface, valid, value: JsonElement) =
        writer.WriteStartObject()
        writer.WriteString("id", (id: string))
        writer.WriteString("surface", (surface: string))
        writer.WriteBoolean("valid", valid)
        writer.WritePropertyName("value")
        value.WriteTo writer
        writer.WriteEndObject()

    let artifact () =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", 1)
        writer.WriteStartArray("cases")

        cli @ web @ adminFailures @ adminInputs @ adminCompletion @ adminDataAudit
        |> List.iter (writeCase writer)

        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()

        "local-diagnostics.parsed-value-corpus.json",
        Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]
