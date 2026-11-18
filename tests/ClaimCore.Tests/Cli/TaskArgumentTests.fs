module ClaimCore.Tests.TaskArgumentTests

open ClaimCore.CommandLine

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Cli
open ClaimCore.Wire
open ClaimCore.TestSupport

let private operation = "019aa6a0-0000-7000-8000-000000000001"

let private prepare args =
    Grammar.prepare (fun () -> operation) args
    |> Result.defaultWith (fun errors -> failtestf "%A" errors)

let private input args =
    Frames.value Api.operationExecute (prepare args).Invocation
    |> Option.defaultWith (fun () -> failtest "Executable operation required")


[<Tests>]
let tests =
    testList
        "typed task argv assembly"
        [
            testCase "case ID grants and revocations retain explicit case scope" (fun () ->
                for verb in [ "grant"; "revoke" ] do
                    let submitted =
                        input
                            [
                                "people"
                                verb
                                "10000000-0000-4000-8000-000000000001"
                                "--role"
                                "case-reader"
                                "--case-id"
                                "10000000-0000-4000-8000-000000000002"
                                "--expected-revision"
                                "1"
                            ]
                        |> Submissions.command

                    Expect.equal
                        submitted.Inputs.Values[TaskInputId.Scope]
                        (InputValue.Text "CASE")
                        "An explicit ID is a case scope"

                    Expect.equal
                        submitted.Inputs.Values[TaskInputId.CaseId]
                        (InputValue.Text "10000000-0000-4000-8000-000000000002")
                        "ID stays tagged")
            testCase
                "existing-case reads and operations accept explicit IDs and refuse dual selection"
                (fun () ->
                    let id = "10000000-0000-4000-8000-000000000002"
                    let query = prepare [ "case"; "get"; "--case-id"; id ]

                    Expect.equal
                        (Frames.value Api.caseGet query.Invocation |> Option.map _.Target)
                        (Some(CaseSelector.Id(Guid.Parse id |> CaseId.create |> Option.get)))
                        "Read selection"

                    let submitted =
                        input
                            [
                                "case"
                                "close"
                                "--outcome"
                                "other"
                                "--case-id"
                                id
                                "--expected-revision"
                                "3"
                            ]

                    match submitted.Envelope.Binding with
                    | OperationBinding.Case(CaseSelector.Id actual, 3L, None, None) ->
                        Expect.equal (CaseId.format actual) id "Operation selection"
                    | _ -> failtest "ID binding required"

                    Expect.isError
                        (Grammar.prepare
                            (fun () -> operation)
                            [ "case"; "get"; "REFERENCE"; "--case-id"; id ])
                        "Conflicting selectors refuse")
            testCase "payment task retains exact precision and explicit reviewed version" (fun () ->
                let value =
                    input
                        [
                            "case"
                            "record-payment"
                            "TASK"
                            "--expected-revision"
                            "7"
                            "--payment-date"
                            "2026-11-09"
                            "--paid-amount"
                            "1.2300"
                            "--paid-currency"
                            "EUR"
                        ]

                Expect.equal value.OperationId (Guid.Parse operation) "Allocated identity"
                let command = Submissions.command value

                Expect.equal
                    command.Binding
                    (Binding.Case(CaseSelector.Reference "TASK", Revision.create 7L |> Option.get))
                    "Authored selector and version"

                Expect.equal
                    command.Inputs.Values
                    (Map.ofList
                        [
                            TaskInputId.Target(InputTarget.Field BusinessField.PaymentDate),
                            InputValue.Text "2026-11-09"
                            TaskInputId.Target(InputTarget.Field BusinessField.PaidAmount),
                            InputValue.Text "1.2300"
                            TaskInputId.Target(InputTarget.Field BusinessField.PaidCurrency),
                            InputValue.Text "EUR"
                        ])
                    "No authored precision normalization")
            testCase
                "correction preserves separate keep clear and selected scalar intent"
                (fun () ->
                    let value =
                        input
                            [
                                "case"
                                "correct"
                                "TASK"
                                "--expected-revision"
                                "7"
                                "--registration"
                                "update"
                                "--clear-policy-number"
                                "--policy-number"
                                "Edited claimant"
                                "--decision"
                                "clear"
                            ]
                        |> Submissions.command

                    Expect.equal
                        value.Inputs.Values
                        (Map.ofList
                            [
                                TaskInputId.Target(InputTarget.Group RecordGroup.Registration),
                                InputValue.Text "UPDATE"
                                TaskInputId.Target(InputTarget.Group RecordGroup.Decision),
                                InputValue.Text "CLEAR"
                                TaskInputId.Target(InputTarget.Field BusinessField.PolicyNumber),
                                InputValue.Null
                                TaskInputId.Target(InputTarget.Field BusinessField.PolicyNumber),
                                InputValue.Text "Edited claimant"
                            ])
                        "Omitted fields keep, explicit null clears")
            testCase "compound roles retain submitted order and explicit scope spelling" (fun () ->
                let value =
                    input
                        [
                            "people"
                            "admit"
                            "--principal-kind"
                            "human"
                            "--issuer"
                            "https://issuer.example/"
                            "--subject"
                            "typed-target"
                            "--name"
                            "Editable name"
                            "--role"
                            "case-reader@CASE-A"
                            "--role"
                            "auditor"
                        ]
                    |> Submissions.command

                Expect.equal
                    value.Inputs.Values[TaskInputId.Target InputTarget.ProfileDisplayName]
                    (InputValue.Text "Editable name")
                    "Profile input"

                Expect.equal
                    (value.Inputs.Collections[TaskCollectionId.PeopleGrants] |> List.map _.Values)
                    [
                        Map.ofList
                            [
                                TaskInputId.Target InputTarget.Role, InputValue.Text "CASE_READER"
                                TaskInputId.Scope, InputValue.Text "CASE"
                                TaskInputId.Target(InputTarget.Field BusinessField.CaseReference),
                                InputValue.Text "CASE-A"
                            ]
                        Map.ofList
                            [
                                TaskInputId.Target InputTarget.Role, InputValue.Text "AUDITOR"
                                TaskInputId.Scope, InputValue.Text "INSTALLATION"
                            ]
                    ]
                    "Submitted ordinals")
            testCase
                "malformed metadata and unselected fields report usage without throwing or fabricating commands"
                (fun () ->
                    for args in
                        [
                            [
                                "people"
                                "set-profile"
                                "--principal-kind"
                                "human"
                                "--issuer"
                                "https://issuer.example/"
                                "--subject"
                                "duplicate-contact"
                                "--name"
                                "Name"
                                "--contact"
                                "set"
                                "--clear-contact"
                            ]
                            [
                                "case"
                                "close"
                                "--outcome"
                                "other"
                                "TASK"
                                "--expected-revision"
                                "-1"
                            ]
                            [
                                "case"
                                "close"
                                "--outcome"
                                "other"
                                "TASK"
                                "--expected-revision"
                                "9007199254740992"
                            ]
                            [
                                "case"
                                "correct"
                                "TASK"
                                "--expected-revision"
                                "1"
                                "--decision"
                                "keep"
                                "--payable-amount"
                                "10"
                            ]
                            [
                                "case"
                                "amend-registration"
                                "TASK"
                                "--expected-revision"
                                "1"
                                "--claimed-amount"
                                "10"
                            ]
                            [
                                "reminder"
                                "complete"
                                "TASK"
                                "--reminder-id"
                                "00000000-0000-4000-8000-000000000001"
                                "--expected-reminder-revision"
                                "0"
                            ]
                            [
                                "case"
                                "amend-registration"
                                "TASK"
                                "--expected-revision"
                                "1"
                                "--clear-claimed-amount"
                            ]
                        ] do
                        Expect.isError
                            (Grammar.prepare (fun () -> operation) args)
                            ("Usage failure before executable delivery: " + String.concat " " args))
        ]

[<Tests>]
let creationBasis =
    testCase "case creation never acquires an authored expected revision" (fun () ->
        let task = "case.open"

        let revision =
            CommandGrammar.inputs task
            |> List.find (fun input ->
                input.Name = TaskInputId.Target InputTarget.ExpectedRevision)

        Expect.equal
            revision.Argument
            CommandArgument.Implicit
            "The fixed creation basis is not an argv input"

        Expect.isFalse
            ((CommandGrammar.synopsis task)
                .Contains("--expected-revision", StringComparison.Ordinal))
            "Help offers no ignored version override"

        match
            Grammar.prepare
                (fun () -> operation)
                [ "case"; "open"; "TASK"; "--expected-revision"; "7" ]
        with
        | Error issues ->
            Expect.isTrue
                (issues
                 |> List.exists (function
                     | ClaimCore.CommandLine.UsageError.UnknownOption("--expected-revision", _) ->
                         true
                     | _ -> false))
                "Supplying the retired override is a specific syntax refusal"
        | Ok _ -> failtest "An authored version cannot override creation")
