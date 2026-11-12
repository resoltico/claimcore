module ClaimCore.Tests.CliExamples

open ClaimCore.Application.Contracts
open Expecto
open ClaimCore.Cli
open ClaimCore.Wire
open ClaimCore.Presentation
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
    |> fun fields ->
        fields
        @ [ "--name", "Authored"; "--contact", "Contact"; "--role", "CASE_READER" ]
    |> Map.ofList

/// Fill only omitted business inputs. Grammar still owns branch selection and required syntax.
let complete arguments =
    let rec prepare remaining arguments =
        match Grammar.prepare (fun () -> "11111111-2222-7333-8444-555555555555") arguments with
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

    prepare CaseFields.declarations.Length arguments

let private inputSamples =
    Map.ofList
        [
            "caseReference", "SYNTHETIC"
            "operationId", "10000000-0000-7000-8000-000000000001"
            "actorId", "10000000-0000-7000-8000-000000000001"
            "limit", "5"
            "cursor", "synthetic"
            "detail", "FULL"
            "lifecycle", "ACTIVE"
            "status", "OPENED"
            "progress", "UNDECIDED"
            "text", "synthetic"
            "order", "REFERENCE"
            "responsibleActor", "00000000-0000-7000-8000-000000000042"
            "handlingSummary", "Current handling context"
            "incidentDescription", "Incident details"
            "noteText", "Authored note"
            "noteId", "10000000-0000-7000-8000-000000000007"
            "reminderAction", "Contact insurer"
            "reminderDueDate", "2026-11-10"
            "reminderId", "10000000-0000-7000-8000-000000000007"
            "expectedWorkRevision", "0"
            "expectedReminderRevision", "1"
            "profile.displayName", "Authored"
            "profile.contact", "Contact"
            "principal.kind", "HUMAN"
            "principal.issuer", "https://issuer.example/"
            "principal.subject", "waiting"
        ]

let private queryInput (input: CommandInput) =
    let key = TaskInputId.key input.Name
    let sample = Map.tryFind key inputSamples
    let flag = "--" + CommandGrammar.kebab (CommandGrammar.inputName input.Name)

    match input.Argument, sample with
    | CommandArgument.Implicit, _ -> []
    | _, None when input.Required ->
        failtestf "CLI input builders need a sample for required input %s" key
    | _, None -> []
    | CommandArgument.Operand, Some value -> [ value ]
    | CommandArgument.Named, Some value -> [ flag; value ]

let queryArguments operation =
    let inputs = CommandGrammar.inputs operation
    CommandGrammar.words operation @ (inputs |> List.collect queryInput)
