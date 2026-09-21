namespace ClaimCore.ContractGeneration

open System
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Qualification

module internal DiagnosticCorpus =
    let private parsed (bytes: byte array) =
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
        document.RootElement.Clone()

    let private replace name (replacement: JsonElement) value =
        CorpusJson.rewrite value name (Some(fun writer -> replacement.WriteTo(writer))) false

    let private text value =
        JsonSerializer.SerializeToElement(value: string)

    let private empty = JsonSerializer.SerializeToElement(Map.empty<string, int>)

    let private argumentVariants (diagnostic: JsonElement) =
        let parameters = diagnostic.GetProperty("parameters")

        parameters.EnumerateObject()
        |> Seq.toList
        |> List.collect (fun parameter ->
            [
                "string", text "200"
                "fractional", JsonSerializer.SerializeToElement(1.5)
                "negative", JsonSerializer.SerializeToElement(-1)
                "overflow", JsonSerializer.SerializeToElement(2147483648L)
            ]
            |> List.map (fun (suffix, value) ->
                "argument-" + parameter.Name + "-" + suffix,
                diagnostic |> replace "parameters" (parameters |> replace parameter.Name value)))

    let private variants property (root: JsonElement) =
        let outcome = root.GetProperty("outcome")
        let rejection = outcome.GetProperty(property: string)
        let diagnostic = rejection.GetProperty("diagnostic")
        let parameters = diagnostic.GetProperty("parameters")

        let changedDiagnostic =
            [
                "unknown-id", diagnostic |> replace "id" (text "UNDECLARED_DIAGNOSTIC")
                "extra-diagnostic", CorpusJson.rewrite diagnostic "" None true
                "extra-parameter",
                diagnostic |> replace "parameters" (CorpusJson.rewrite parameters "" None true)
                "wrong-parameters",
                diagnostic
                |> replace
                    "parameters"
                    (if parameters.EnumerateObject() |> Seq.isEmpty then
                         JsonSerializer.SerializeToElement({| maximumCharacters = 200 |})
                     else
                         empty)
            ]
            @ argumentVariants diagnostic

        let changedRejections =
            (changedDiagnostic
             |> List.map (fun (id, value) -> id, false, replace "diagnostic" value rejection))
            @ [
                "missing-diagnostic", false, CorpusJson.rewrite rejection "diagnostic" None false
                "unknown-field", false, replace "field" (text "undeclared-private-field") rejection
                "translated-copy",
                true,
                replace "message" (text "Ievade nav pieņemta — العربية") rejection
            ]

        changedRejections
        |> List.map (fun (id, valid, value) ->
            id, valid, root |> replace "outcome" (replace property value outcome))

    let private cases property encode =
        RejectionExamples.all
        |> List.collect (fun (id, rejection) ->
            let value = encode rejection |> parsed

            ("diagnostic-" + id, true, value)
            :: (variants property value
                |> List.map (fun (suffix, valid, item) ->
                    "diagnostic-" + id + "-" + suffix, valid, item)))

    let cli =
        cases "rejection" (fun rejection ->
            (CliWireCodec.caseGet "case.get" (QueryOutcome.Rejected rejection)).Bytes)

    let web =
        cases "data" (fun rejection -> WebWireCodec.get (QueryOutcome.Rejected rejection))
