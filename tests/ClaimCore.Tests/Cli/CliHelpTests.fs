module ClaimCore.Tests.CliHelpTests

open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Cli

let private descriptions () =
    for language in LocalizationPolicy.languages do
        for operation in CommandGrammar.operations do
            let words = CommandGrammar.words operation

            let output = Discovery.commandHelp language words |> Option.get

            Expect.stringContains
                output
                (CommandGrammar.synopsis operation)
                "Description uses the operation grammar"

            let inputs = CommandGrammar.inputs operation

            for input in inputs do
                if input.Argument <> CommandArgument.Implicit then
                    Expect.stringContains
                        output
                        (ContractDiscovery.localized language (input.LabelId))
                        "Every admitted input has its localized label"

            Expect.isFalse (output.Contains "CommandInput") "Internal types do not leak into help"

[<Tests>]
let tests =
    testCase "every operation describes its actual inputs in all languages" descriptions

[<Tests>]
let semanticHelp =
    testCase
        "JSON command help supplies semantic task controls without wire paths or catalog aliases"
        (fun () ->
            let bytes = Discovery.commandHelpJson "en" [ "authority"; "admit" ] |> Option.get
            use document = System.Text.Json.JsonDocument.Parse(bytes: byte array)
            let operation = document.RootElement.GetProperty("operations")[0]

            Expect.isFalse
                (operation.TryGetProperty("catalog") |> fst)
                "Retired transport binding is not a compatibility output"

            let task = operation.GetProperty("task")

            Expect.equal
                (task.GetProperty("id").GetString())
                "AUTHORITY:ADMIT"
                "Semantic task identity"

            let collection = task.GetProperty("collections")[0]

            Expect.equal
                (collection.GetProperty("id").GetString())
                "authorityGrants"
                "Semantic collection"

            Expect.equal
                (collection.GetProperty("minimumRows").GetInt32())
                1
                "A compound admission includes a role"

            Expect.equal
                (collection.GetProperty("maximumRows").GetInt32())
                48
                "Authored collection bound"

            Expect.isFalse
                (document.RootElement
                    .GetRawText()
                    .Contains("\"path\"", System.StringComparison.Ordinal))
                "No member paths define controls"

            Expect.isFalse
                (document.RootElement
                    .GetRawText()
                    .Contains("\"branches\"", System.StringComparison.Ordinal))
                "No wire branch interpreter")
