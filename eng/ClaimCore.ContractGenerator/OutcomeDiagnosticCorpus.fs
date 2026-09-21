namespace ClaimCore.ContractGeneration

open System
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Contracts

/// Real codec values, with independent hostile mutations at each diagnostic boundary.
module internal OutcomeDiagnosticCorpus =
    let private parsed (bytes: byte array) =
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
        document.RootElement.Clone()

    let private replace name (replacement: JsonElement) value =
        CorpusJson.rewrite value name (Some(fun writer -> replacement.WriteTo(writer))) false

    let private text value =
        JsonSerializer.SerializeToElement(value: string)

    let private policyVariants (value: JsonElement) =
        let action = value.GetProperty("recommendedAction").GetString()

        let wrongAction =
            if action = "RETRY_SAFE" then
                "STOP_AND_INVESTIGATE"
            else
                "RETRY_SAFE"

        let code = value.GetProperty("code").GetString()

        let wrongCode =
            if code = "STORE_UNAVAILABLE" then
                "SCHEMA_MISMATCH"
            else
                "STORE_UNAVAILABLE"

        [
            "contradictory-code", false, replace "code" (text wrongCode) value
            "contradictory-action", false, replace "recommendedAction" (text wrongAction) value
        ]

    let private variants (value: JsonElement) =
        let diagnostic = value.GetProperty("diagnostic")

        [
            "unknown-id",
            false,
            replace "diagnostic" (replace "id" (text "UNDECLARED") diagnostic) value
            "cross-family-id",
            false,
            replace "diagnostic" (replace "id" (text "INPUT_TEXT_REQUIRED") diagnostic) value
            "missing-id",
            false,
            replace "diagnostic" (CorpusJson.rewrite diagnostic "id" None false) value
            "missing-diagnostic", false, CorpusJson.rewrite value "diagnostic" None false
            "extra-diagnostic",
            false,
            replace "diagnostic" (CorpusJson.rewrite diagnostic "" None true) value
            "missing-parameters",
            false,
            replace "diagnostic" (CorpusJson.rewrite diagnostic "parameters" None false) value
            "null-parameters",
            false,
            replace
                "diagnostic"
                (replace
                    "parameters"
                    (JsonSerializer.SerializeToElement(null: string | null))
                    diagnostic)
                value
            "extra-parameter",
            false,
            replace
                "diagnostic"
                (replace
                    "parameters"
                    (JsonSerializer.SerializeToElement({| secret = "DO-NOT-ADMIT" |}))
                    diagnostic)
                value
            "translated-copy", true, replace "message" (text "Atbilde — العربية") value
        ]

    let private cases property identifier bytes =
        let root = parsed bytes
        let outcome = root.GetProperty("outcome")
        let value = outcome.GetProperty(property: string)

        (identifier, true, root)
        :: ((variants value @ policyVariants value)
            |> List.map (fun (suffix, valid, item) ->
                identifier + "-" + suffix,
                valid,
                replace "outcome" (replace property item outcome) root))

    let private cliFaults =
        CoreFaults.all
        |> List.collect (fun (fault, id) ->
            let response = CliWireCodec.caseGet "case.get" (QueryOutcome.Failed fault)

            cases "fault" ("fault-" + id) response.Bytes
            |> List.map (fun (name, valid, value) ->
                name, "case.get", response.ExitCode, valid, value))

    let private cliRefusals =
        RecoveryRejections.all
        |> List.collect (fun (reason, id) ->
            let response =
                CliWireCodec.recoveryList
                    "recovery.list"
                    (RecoveryQueryOutcome.RecoveryRejected reason)

            cases "rejection" ("recovery-diagnostic-" + id) response.Bytes
            |> List.map (fun (name, valid, value) ->
                name, "recovery.list", response.ExitCode, valid, value))

    let private cliLocals =
        (ContractProjection.current ()).CliEndpoints
        |> List.collect (fun endpoint ->
            CliLocalFaults.all
            |> List.collect (fun fault ->
                let response = CliWireCodec.localFailure endpoint.Identifier fault

                cases
                    "fault"
                    ("local-" + endpoint.Identifier + "-" + CliLocalFaults.token fault)
                    response.Bytes
                |> List.map (fun (name, valid, value) ->
                    name, endpoint.Identifier, response.ExitCode, valid, value)))

    let cli = cliFaults @ cliRefusals @ cliLocals

    let web =
        (CoreFaults.all
         |> List.collect (fun (fault, id) ->
             cases "data" ("fault-" + id) (WebWireCodec.get (QueryOutcome.Failed fault))
             |> List.map (fun (name, valid, value) -> name, "case.get", valid, value)))
        @ (RecoveryRejections.all
           |> List.collect (fun (reason, id) ->
               cases
                   "data"
                   ("recovery-diagnostic-" + id)
                   (WebWireCodec.recoveryList (RecoveryQueryOutcome.RecoveryRejected reason))
               |> List.map (fun (name, valid, value) -> name, "recovery.list", valid, value)))
