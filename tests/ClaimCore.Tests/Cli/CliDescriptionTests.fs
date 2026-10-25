module ClaimCore.Tests.CliDescriptionTests

open System.Text
open Expecto
open ClaimCore.Contract
open ClaimCore.Cli

let private descriptions () =
    for language in LocalizationPolicy.languages do
        for operation in OperationCatalog.all do
            let selection = CliDescription.Operation(OperationCatalog.words operation)

            let output =
                Discovery.describeText language selection
                |> Option.get
                |> Encoding.UTF8.GetString

            Expect.stringContains
                output
                (OperationCatalog.synopsis operation)
                "Description uses the operation grammar"

            let inputs, _ = OperationCatalog.derive operation

            for input in inputs do
                if input.Spec.Argument <> CatalogArgument.Implicit then
                    Expect.stringContains
                        output
                        (ContractDiscovery.localized language (input.Binding.LabelId))
                        "Every admitted input has its localized label"

            Expect.isFalse (output.Contains "CatalogShape") "Internal types do not leak into help"

[<Tests>]
let tests =
    testCase "every operation describes its actual inputs in all languages" descriptions
