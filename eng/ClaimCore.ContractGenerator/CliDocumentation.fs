namespace ClaimCore.ContractGeneration

open System.Text
open ClaimCore.Contract

module CliDocumentation =
    let private argument (input: CatalogDerivedInput) =
        let name = OperationCatalog.inputKey input.Name |> OperationCatalog.kebab

        match input.Argument with
        | CatalogArgument.Named -> "--" + name
        | CatalogArgument.Operand -> name.ToUpperInvariant()
        | CatalogArgument.Implicit -> ""

    let source () =
        let english = CatalogValidation.validate () |> Map.find "en"
        let message key = Template.render english[key] Map.empty

        let escape (text: string) =
            text.Replace("|", "\\|").Replace("\n", " ")

        let describe operation =
            let inputs, _ = OperationCatalog.derive operation

            [
                "## " + (OperationCatalog.words operation |> String.concat " ")
                ""
                message (OperationCatalog.labelId operation)
                ""
                message (OperationCatalog.meaningId operation)
                ""
                "```sh"
                "claimcore " + OperationCatalog.synopsis operation
                "```"
                ""
                "| Input | Meaning | Choices | Default |"
                "| --- | --- | --- | --- |"
                yield!
                    inputs
                    |> List.filter (fun input -> input.Argument <> CatalogArgument.Implicit)
                    |> List.map (fun input ->
                        let choices =
                            match input.Atom with
                            | CatalogAtom.Choice choices ->
                                choices |> List.map OperationCatalog.kebab |> String.concat ", "
                            | _ -> ""

                        let defaultValue =
                            match input.Binding.Default with
                            | Some(BindingValue.Integer value) -> string value
                            | Some(BindingValue.Text value) -> value
                            | Some(BindingValue.Boolean value) -> string value
                            | None -> ""

                        "| `"
                        + argument input
                        + "` | "
                        + escape (message input.Binding.LabelId)
                        + " — "
                        + escape (
                            input.Binding.MeaningId |> Option.map message |> Option.defaultValue ""
                        )
                        + " | "
                        + escape choices
                        + " | "
                        + escape defaultValue
                        + " |")
                ""
            ]

        [
            "# CLI reference"
            ""
            "Generated from the typed Contract operation catalog. Regenerate with `dotnet run --project eng/ClaimCore.ContractGenerator -- --output contracts`; generator `--check` also checks this file."
            ""
            "A synopsis shows required inputs and brackets optional inputs. Choices use lowercase kebab spelling; exact uppercase wire tokens are also accepted. Mutation operations generate an operation ID when omitted. Keep that ID and the exact prepared frame for retries."
            ""
            "Use `--dry-run` to print the prepared JSON frame without authentication. This checks protocol shape and byte limits; domain admission, authorization and current revision are checked by the service. Use `--preview` with a case or authority act to request a review without recording it."
            ""
            "Human operations default to text. Add `--format json` for structured output or `--language en|lv|ar` to select interface text. Display language does not rewrite authored values."
            ""
            "Top-level `claimcore` lists command families. `claimcore describe case open` describes an operation offline; `claimcore schema operation.execute request` shows its wire schema. `claimcore call` reads one JSON frame from stdin; `claimcore session` reads NDJSON."
            ""
            yield! OperationCatalog.all |> List.collect describe
        ]
        |> String.concat "\n"
        |> fun text -> Encoding.UTF8.GetBytes(text + "\n")
