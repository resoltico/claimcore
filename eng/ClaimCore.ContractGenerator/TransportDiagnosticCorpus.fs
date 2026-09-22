namespace ClaimCore.ContractGeneration

open System
open System.Text.Json
open ClaimCore.Contracts

module internal TransportDiagnosticCorpus =
    let private parsed (bytes: byte array) =
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
        document.RootElement.Clone()

    let private text (value: string) =
        JsonSerializer.SerializeToElement(value)

    let private replace name (replacement: JsonElement) value =
        CorpusJson.rewrite value name (Some(fun writer -> replacement.WriteTo writer)) false

    let private variants (value: JsonElement) =
        let diagnostic = value.GetProperty("diagnostic")

        [
            "translated-copy", true, replace "message" (text "Atbilde — العربية") value
            "missing-diagnostic", false, CorpusJson.rewrite value "diagnostic" None false
            "unknown-id",
            false,
            replace "diagnostic" (replace "id" (text "UNDECLARED") diagnostic) value
            "missing-id",
            false,
            replace "diagnostic" (CorpusJson.rewrite diagnostic "id" None false) value
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
            "extra-parameters",
            false,
            replace
                "diagnostic"
                (replace
                    "parameters"
                    (JsonSerializer.SerializeToElement({| secret = "DO-NOT-ADMIT" |}))
                    diagnostic)
                value
            "extra-diagnostic",
            false,
            replace "diagnostic" (CorpusJson.rewrite diagnostic "" None true) value
            "wrong-code", false, replace "code" (text "UNKNOWN_CODE") value
        ]

    let cli =
        ProtocolProblems.all
        |> List.collect (fun reason ->
            let id = "transport-" + ProtocolProblems.token reason

            let response =
                CliWireCodec.protocolFailure
                    2
                    (ProtocolFailure.create reason ProtocolLocation.root)

            let value = parsed response.Bytes

            (id, true, value)
            :: ((variants value
                 @ [
                     "unsafe-path",
                     false,
                     replace "path" (text "/PRIVATE-UNRECOGNIZED-INPUT") value
                 ])
                |> List.map (fun (suffix, valid, item) -> id + "-" + suffix, valid, item)))

    let web =
        WebHostFailures.all
        |> List.collect (fun reason ->
            let id = "host-diagnostic-" + WebHostFailures.token reason
            let status = WebHostFailures.status reason
            let otherStatus = if status = 400 then 413 else 400
            let value = WebWireCodec.hostFailure reason |> parsed

            let ordinary =
                variants value
                @ [
                    "wrong-body-status",
                    false,
                    replace "status" (JsonSerializer.SerializeToElement(otherStatus)) value
                    "wrong-phase", false, replace "executionPhase" (text "UNKNOWN_PHASE") value
                ]

            (id, status, true, value)
            :: (id + "-wrong-http-status", otherStatus, false, value)
            :: (ordinary
                |> List.map (fun (suffix, valid, item) -> id + "-" + suffix, status, valid, item)))
