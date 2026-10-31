namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// One public code identifies one message and one parameter contract across all diagnostic families.
type DiagnosticCode =
    {
        Code: string
        MessageKey: string
        Parameters: DiagnosticParameterDefinition list
    }

module DiagnosticCode =
    let create entries =
        let grouped = entries |> List.groupBy _.Code

        if grouped |> List.exists (fun (_, values) -> values.Length <> 1) then
            invalidArg (nameof entries) "Diagnostic codes must be unique across families."

        if
            entries
            |> List.exists (fun entry ->
                System.String.IsNullOrWhiteSpace entry.Code
                || System.String.IsNullOrWhiteSpace entry.MessageKey)
        then
            invalidArg (nameof entries) "Diagnostic codes and message keys must be nonempty."

        entries |> List.map (fun entry -> entry.Code, entry) |> Map.ofList

    let private family name definitions =
        definitions
        |> List.map (fun (definition: DiagnosticDefinition) ->
            {
                Code = definition.Id
                MessageKey = name + "." + definition.Id
                Parameters = definition.Parameters
            })

    let all =
        [
            yield! family "diagnostic" DiagnosticCodecs.definitions
            yield! family "diagnostic" DiagnosticCodecs.violationDefinitions
            yield! family "fault" DiagnosticCodecs.faultDefinitions
            for value in Vocabulary.all HostFailureCode.vocabulary do
                let code = Vocabulary.token HostFailureCode.vocabulary value

                yield
                    {
                        Code = code
                        MessageKey = "host." + code
                        Parameters = []
                    }
        ]
        |> create

    let key code =
        Map.tryFind code all
        |> Option.map _.MessageKey
        |> Option.defaultWith (fun () ->
            invalidArg (nameof code) "An undeclared diagnostic code cannot be rendered.")
