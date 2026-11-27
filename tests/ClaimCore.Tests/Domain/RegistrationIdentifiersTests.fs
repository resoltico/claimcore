module ClaimCore.Tests.RegistrationIdentifiersTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.TestSupport

let private required value =
    value |> Result.defaultWith (fun error -> failtestf "%A" error)

let private today = DateOnly(2026, 9, 7)

let private values members =
    members
    |> List.map (fun (field, value) -> TaskInputId.Target(InputTarget.Field field), value)
    |> Map.ofList

let private text members =
    members
    |> List.map (fun (field, value) -> field, InputValue.Text value)
    |> values

let private run number revision task members previous =
    let context = ClaimCore.Tests.TaskBuilders.context number

    let context =
        { context with
            Decision =
                { context.Decision with
                    BusinessDate = Some today
                }
        }

    let id =
        previous
        |> Option.map (fun (state: ClaimCore.Domain.Aggregates.CaseFamily) -> state.Id)
        |> Option.defaultValue (CaseId.ofOperationId context.Stamp.OperationId)

    let binding =
        if task = "case.open" then
            Binding.CaseNew Revision.zero
        elif task = "case.erase" then
            Binding.CaseErase(
                CaseSelector.Id id,
                Revision.create revision |> Option.get,
                previous.Value.Sequence
            )
        else
            Binding.Case(CaseSelector.Id id, Revision.create revision |> Option.get)

    let members =
        if task = "case.open" then
            members
            |> Map.add
                (TaskInputId.Target(InputTarget.Field BusinessField.CaseReference))
                (InputValue.Text "IDENTIFIERS")
        else
            members

    let input = InputMap.create members Map.empty |> Option.get

    let environment = ClaimCore.Tests.TaskBuilders.environment

    let state =
        previous |> Option.map ClaimCore.Domain.Aggregates.DecisionFacts.fromReplay

    let changes =
        match ClaimCore.Domain.Tasks.Registry.find (TaskId.ofLiteral task) with
        | Some(ClaimCore.Domain.Tasks.Task.Opening _) ->
            ClaimCore.Tests.TaskBuilders.openCase context environment state binding input
        | Some(ClaimCore.Domain.Tasks.Task.Family task) ->
            task.Decide context environment state binding input
        | _ -> failtest "Expected family task."

    changes
    |> required
    |> List.fold
        (fun state change ->
            Some(ClaimCore.Domain.Aggregates.CaseFamily.apply state change |> required))
        previous
    |> Option.get

let private snapshot record =
    ClaimCore.Domain.Aggregates.CaseFamily.snapshot record |> Option.get

let private registration record =
    (snapshot record).State |> CaseState.fields |> Option.get |> _.Registration

let private facts =
    DomainInputs.registrationWith
        [
            BusinessField.PolicyNumber, "LV / Polise 42"
            BusinessField.InsurerClaimReference, "ملف / 17"
        ]
        DomainInputs.registration

[<Tests>]
let tests =
    testList
        "declared registration identifiers"
        [
            testCase
                "optional identifiers open amend correct and erase through ordinary field inputs"
                (fun () ->
                    let absent =
                        run
                            1
                            0L
                            "case.open"
                            (text (
                                RegistrationInput.values DomainInputs.registration
                                |> List.filter (fun (field, _) ->
                                    field <> BusinessField.PolicyNumber
                                    && field <> BusinessField.InsurerClaimReference)
                            ))
                            None

                    Expect.isNone
                        (Registration.policyNumber (registration absent))
                        "Omitted policy is absent"

                    let opened = run 2 0L "case.open" (text (RegistrationInput.values facts)) None

                    Expect.equal
                        (Registration.policyNumber (registration opened)
                         |> Option.map Identifier.format)
                        (Some "LV / Polise 42")
                        "Authored policy"

                    Expect.equal
                        (Registration.insurerClaimReference (registration opened)
                         |> Option.map Identifier.format)
                        (Some "ملف / 17")
                        "Unicode insurer reference"

                    let amended =
                        run
                            3
                            1L
                            "case.amendRegistration"
                            (values [ BusinessField.PolicyNumber, InputValue.Text "Policy 43" ])
                            (Some opened)

                    Expect.equal
                        (Registration.policyNumber (registration amended)
                         |> Option.map Identifier.format)
                        (Some "Policy 43")
                        "Only selected fact changes"

                    Expect.equal
                        (Registration.insurerClaimReference (registration amended)
                         |> Option.map Identifier.format)
                        (Some "ملف / 17")
                        "Omitted fact stays"

                    let decided =
                        run
                            4
                            2L
                            "case.decide"
                            (text (DecisionInput.values DomainInputs.decision))
                            (Some amended)

                    let corrected =
                        run
                            5
                            3L
                            "case.correct"
                            (values [ BusinessField.PolicyNumber, InputValue.Null ]
                             |> Map.add
                                 (TaskInputId.Target(InputTarget.Group RecordGroup.Registration))
                                 (InputValue.Text "UPDATE")
                             |> Map.add
                                 (TaskInputId.Target(InputTarget.Group RecordGroup.Decision))
                                 (InputValue.Text "KEEP"))
                            (Some decided)

                    Expect.isNone
                        (Registration.policyNumber (registration corrected))
                        "Explicit optional clear"

                    Expect.equal
                        (Registration.insurerClaimReference (registration corrected)
                         |> Option.map Identifier.format)
                        (Some "ملف / 17")
                        "Correction preserves the other identifier"

                    let erased = run 6 4L "case.erase" Map.empty (Some corrected)

                    Expect.equal
                        (snapshot erased).State
                        CaseState.Erased
                        "Erasure retains no registration facts")
            testCase
                "identifier admission counts Unicode scalars and refuses whitespace controls and excess"
                (fun () ->
                    let admit value =
                        Identifier.parse value |> Result.bind Identifier.admit

                    for value in
                        [ String.replicate 80 "a"; String.replicate 80 "😀"; "Police / رقم 7" ] do
                        Expect.equal
                            (admit value |> Result.map Identifier.format)
                            (Ok value)
                            "Valid exact authored identifier"

                    for value in
                        [
                            ""
                            " leading"
                            "trailing "
                            "line\nbreak"
                            "tab\tvalue"
                            String.replicate 81 "a"
                            String.replicate 81 "😀"
                        ] do
                        Expect.isError (admit value) "Invalid identifier is refused")
        ]
