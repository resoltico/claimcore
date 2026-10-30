module ClaimCore.Tests.CliExamples

open Expecto
open ClaimCore.Cli
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.TestSupport

let registrationArguments input =
    RegistrationInput.values input
    |> List.collect (fun (field, value) ->
        [ "--" + CommandGrammar.kebab (BusinessField.key field); value ])

let private fieldSamples =
    (RegistrationInput.values Inputs.registration
     @ DecisionInput.values Inputs.decision
     @ PaymentInput.values Inputs.payment)
    |> List.map (fun (field, value) ->
        // Captured argv columns are space-delimited. Only omitted inputs use this synthetic
        // single-word default; arguments explicitly authored by a test remain untouched.
        let sample = if value.Contains(' ') then "Synthetic" else value
        "--" + CommandGrammar.kebab (BusinessField.key field), sample)
    |> Map.ofList

/// Fill only omitted business inputs. Grammar still owns branch selection and required syntax.
let complete arguments =
    let rec prepare remaining arguments =
        match Grammar.prepare (fun () -> "11111111-2222-4333-8444-555555555555") arguments with
        | Ok _ -> arguments
        | Error issues ->
            let additions =
                issues
                |> List.choose (function
                    | UsageError.MissingOption flag ->
                        Map.tryFind flag fieldSamples |> Option.map (fun value -> flag, value)
                    | _ -> None)
                |> List.distinct
                |> List.collect (fun (flag, value) -> [ flag; value ])

            if remaining = 0 || additions.IsEmpty then
                failtestf "Positive CLI arguments require valid technical syntax: %A" issues

            prepare (remaining - 1) (arguments @ additions)

    prepare Inventory.fieldCount arguments

let private queryInput (input: CatalogDerivedInput) =
    let key = InteractionInput.key input.Name
    let sample = Map.tryFind key Inventory.cliSamples
    let flag = "--" + CommandGrammar.kebab (CommandGrammar.inputName input.Name)

    match input.Argument, sample with
    | CatalogArgument.Implicit, _ -> []
    | _, None when input.Binding.Required && input.Binding.Default.IsNone ->
        failtestf "Independent CLI inventory needs a sample for required input %s" key
    | _, None -> []
    | CatalogArgument.Operand, Some value -> [ value ]
    | CatalogArgument.Named, Some value ->
        match input.Atom with
        | CatalogAtom.Switch(off, on) ->
            if value = off then
                []
            elif value = on then
                [ flag ]
            else
                failtestf "Invalid independent switch sample for %s" key
        | _ -> [ flag; value ]

let queryArguments operation =
    let inputs, _ = CommandGrammar.derive operation
    CommandGrammar.words operation @ (inputs |> List.collect queryInput)
