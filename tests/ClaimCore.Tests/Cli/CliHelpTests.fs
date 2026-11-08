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

            let inputs, _ = CommandGrammar.derive operation

            for input in inputs do
                if input.Argument <> CatalogArgument.Implicit then
                    Expect.stringContains
                        output
                        (ContractDiscovery.localized language (input.Binding.LabelId))
                        "Every admitted input has its localized label"

            Expect.isFalse
                (output.Contains "CatalogDerivedInput")
                "Internal types do not leak into help"

[<Tests>]
let tests =
    testCase "every operation describes its actual inputs in all languages" descriptions
