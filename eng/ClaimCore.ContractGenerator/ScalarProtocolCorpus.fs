namespace ClaimCore.ContractGeneration

open System
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Qualification

/// Independent scalar diagnostic examples and hostile local-frame counterparts.
module internal ScalarProtocolCorpus =
    let private parsed (bytes: byte array) =
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
        document.RootElement.Clone()

    let private replace name (value: JsonElement) source =
        CorpusJson.rewrite source name (Some(fun writer -> value.WriteTo writer)) false

    let private text (value: string) =
        JsonSerializer.SerializeToElement(value)

    let private variants (frame: JsonElement) =
        let diagnostic = frame.GetProperty("scalarDiagnostic")
        let parameters = diagnostic.GetProperty("parameters")
        let revised value = replace "scalarDiagnostic" value frame

        let requiredParameter = parameters.EnumerateObject() |> Seq.tryHead

        let missingParameter =
            requiredParameter
            |> Option.map (fun parameter ->
                "missing-scalar-parameter",
                revised (
                    replace
                        "parameters"
                        (CorpusJson.rewrite parameters parameter.Name None false)
                        diagnostic
                ))
            |> Option.toList

        [
            "missing-payload", CorpusJson.rewrite frame "scalarDiagnostic" None false
            "unknown-id", revised (replace "id" (text "UNKNOWN_SCALAR") diagnostic)
            "future-date-id", revised (replace "id" (text "INPUT_FUTURE_DATE") diagnostic)
            "authority-id", revised (replace "id" (text "ACCESS_RESOURCE_UNAVAILABLE") diagnostic)
            "missing-parameters", revised (CorpusJson.rewrite diagnostic "parameters" None false)
            "extra-parameter",
            revised (replace "parameters" (CorpusJson.rewrite parameters "" None true) diagnostic)
            "ordinary-with-payload", replace "diagnosticId" (text "CLI_INVALID_TOKEN") frame
        ]
        @ missingParameter

    let all () =
        RejectionExamples.all
        |> List.collect (fun (id, rejection) ->
            let diagnostic = RejectionDiagnostics.describe rejection

            if not (ScalarAdmissionDiagnostics.supported diagnostic) then
                []
            else
                let frame =
                    ProtocolFailure.scalar
                        diagnostic
                        (ProtocolLocation.fromPath "/input/command/values/claimantName")
                    |> CliRemoteWireCodec.protocolFailure 2
                    |> _.Bytes
                    |> parsed

                ("protocol-scalar-" + id, true, frame)
                :: (variants frame
                    |> List.map (fun (variant, value) ->
                        "protocol-scalar-" + id + "-" + variant, false, value)))
