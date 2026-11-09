module ClaimCore.Tests.TaskAssemblyTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts

let private required result =
    Result.defaultWith (fun error -> failtestf "%A" error) result

let private operation =
    Guid.Parse "019aa6a0-0000-7000-8000-000000000001"
    |> OperationId.create
    |> Option.get

let private basis =
    {
        Operation = operation
        Target = CaseSelector.Reference "TASK-1"
        Revision = Revision.first
    }

[<Tests>]
let tests =
    testList
        "semantic task assembly"
        [
            testCase
                "task and input declarations are finite and independent of transport paths"
                (fun () ->
                    Expect.equal
                        TaskDeclarations.all.Length
                        45
                        "29 authored operations and 16 queries"

                    for declaration in TaskDeclarations.all do
                        let ids = declaration.Inputs |> List.map _.Input

                        Expect.equal
                            (List.distinct ids)
                            ids
                            "One control identity per declared task input"

                    Expect.isNone
                        (TaskRowTarget.create TaskCollectionId.AuthorityGrants -1)
                        "No negative ordinal"

                    Expect.isNone
                        (TaskRowTarget.create TaskCollectionId.AuthorityGrants 48)
                        "Bounded submitted ordinal"

                    let row =
                        TaskRowTarget.create TaskCollectionId.AuthorityGrants 47 |> Option.get

                    Expect.equal (TaskRowTarget.ordinal row) 47 "Last supported submitted row")
            testCase "Keep Clear and empty Set survive typed registration assembly" (fun () ->
                let draft =
                    { RegistrationAmendmentDraft.empty with
                        ClaimantName = TaskUpdate.Set ""
                        IncidentDescription = OptionalTaskUpdate.Clear
                        Claimed = TaskUpdate.Set { Amount = "12.3400"; Currency = "EUR" }
                    }

                let command = TaskAssembly.amendRegistration basis draft |> required

                match command with
                | OperationInput.Case request ->
                    Expect.equal request.ExpectedRevision 1L "The actual reviewed revision"

                    match request.Act with
                    | CaseAct.AmendRegistration update ->
                        Expect.equal
                            (RegistrationUpdateInput.values update |> Map.ofList)
                            (Map.ofList
                                [
                                    BusinessField.ClaimantName, Some ""
                                    BusinessField.IncidentDescription, None
                                    BusinessField.ClaimedAmount, Some "12.3400"
                                    BusinessField.ClaimedCurrency, Some "EUR"
                                ])
                            "Keep is omitted, Clear is explicit, empty Set remains authored, money stays paired"
                    | _ -> failtest "Registration task must assemble its declared action"
                | _ -> failtest "Registration task must assemble a case command"

                Expect.isSome
                    (OperationInput.syntax command)
                    "The core diagnoses the authored empty name")
            testCase
                "payment assembly preserves exact authored dates and money without business admission"
                (fun () ->
                    let command =
                        TaskAssembly.recordPayment
                            basis
                            {
                                Date = "2026-11-01"
                                Paid = { Amount = "0.0001"; Currency = "KWD" }
                            }
                        |> required

                    match command with
                    | OperationInput.Case { Act = CaseAct.RecordPayment payment } ->
                        Expect.equal
                            (PaymentInput.values payment |> Map.ofList)
                            (Map.ofList
                                [
                                    BusinessField.PaymentDate, "2026-11-01"
                                    BusinessField.PaidAmount, "0.0001"
                                    BusinessField.PaidCurrency, "KWD"
                                ])
                            "No floating point conversion or date normalization"
                    | _ -> failtest "Payment task must assemble RECORD_PAYMENT")
            testCase
                "duplicate authority rows retain submitted identity independently of business content"
                (fun () ->
                    let grant =
                        {
                            Role = Role.CaseReader
                            Scope = GrantTarget.CaseReference ""
                        }

                    let rows =
                        [ { Key = "local-b"; Grant = grant }; { Key = "local-a"; Grant = grant } ]

                    let draft =
                        {
                            Target =
                                {
                                    Kind = PrincipalKind.Human
                                    Issuer = "https://issuer.example/"
                                    Subject = "task-person"
                                }
                            Profile =
                                {
                                    DisplayName = "Task person"
                                    Contact = None
                                }
                            Grants = rows
                        }

                    let submission = TaskAssembly.admitAuthority operation draft

                    let changedKeys =
                        TaskAssembly.admitAuthority
                            operation
                            { draft with
                                Grants =
                                    [
                                        { Key = "different-1"; Grant = grant }
                                        { Key = "different-2"; Grant = grant }
                                    ]
                            }

                    Expect.equal
                        submission.Command
                        changedKeys.Command
                        "Local identity is excluded from business input"

                    Expect.equal
                        submission.SubmittedRows[TaskCollectionId.AuthorityGrants]
                        [ "local-b"; "local-a" ]
                        "Submitted order is frozen"

                    let reordered =
                        TaskAssembly.admitAuthority operation { draft with Grants = List.rev rows }

                    Expect.equal
                        reordered.SubmittedRows[TaskCollectionId.AuthorityGrants]
                        [ "local-a"; "local-b" ]
                        "A later submission has its own ordinal map"

                    Expect.equal
                        submission.SubmittedRows[TaskCollectionId.AuthorityGrants]
                        [ "local-b"; "local-a" ]
                        "The previous submission is unchanged")
            testCase
                "authority sibling issues carry submitted ordinals and semantic input identities"
                (fun () ->
                    let grant =
                        {
                            Role = Role.CaseReader
                            Scope = GrantTarget.CaseReference ""
                        }

                    let submission =
                        TaskAssembly.admitAuthority
                            operation
                            {
                                Target =
                                    {
                                        Kind = PrincipalKind.Human
                                        Issuer = "https://issuer.example/"
                                        Subject = "task-person"
                                    }
                                Profile =
                                    {
                                        DisplayName = "Task person"
                                        Contact = None
                                    }
                                Grants =
                                    [
                                        { Key = "first"; Grant = grant }
                                        { Key = "second"; Grant = grant }
                                    ]
                            }

                    match submission.Command with
                    | OperationInput.Authority request ->
                        match Authority.parse request with
                        | Error(Refusal.InvalidInput issues) ->
                            let issues = InputIssues.toList issues

                            Expect.equal
                                issues.Length
                                2
                                "Each submitted invalid row has its own issue"

                            Expect.equal
                                (issues
                                 |> List.map (fun issue ->
                                     issue.GrantRow |> Option.map GrantOrdinal.value))
                                [ Some 0; Some 1 ]
                                "Submitted ordinals, not inferred row identity"

                            let human = issues |> List.map TaskInputIssue.ofDomain

                            Expect.equal
                                (human |> List.map _.Input)
                                [
                                    TaskInputId.Target(
                                        InputTarget.Field BusinessField.CaseReference
                                    )
                                    TaskInputId.Target(
                                        InputTarget.Field BusinessField.CaseReference
                                    )
                                ]
                                "Semantic input target"

                            Expect.equal
                                (human
                                 |> List.map (fun issue ->
                                     issue.Row |> Option.map TaskRowTarget.ordinal))
                                [ Some 0; Some 1 ]
                                "Typed HUMAN row targets retain both identical failures"
                        | other -> failtestf "Expected row input issues, got %A" other
                    | _ -> failtest "Authority task must assemble authority input")
            testCase "typed correction keeps updates and decision clearing explicit" (fun () ->
                for decision in
                    [
                        Change.Keep
                        Change.Clear
                        Change.Update
                            {
                                PaymentDecisionDate = TaskUpdate.Set ""
                                Payable = TaskUpdate.Set { Amount = "10.00"; Currency = "EUR" }
                            }
                    ] do
                    let command =
                        TaskAssembly.correct
                            basis
                            {
                                Registration = Amendment.Keep
                                Decision = decision
                            }
                        |> required

                    match command with
                    | OperationInput.Case { Act = CaseAct.Correct correction } ->
                        Expect.equal
                            correction.Registration
                            Amendment.Keep
                            "The registration group remains omitted"

                        match decision, correction.Decision with
                        | Change.Keep, Change.Keep
                        | Change.Clear, Change.Clear -> ()
                        | Change.Update _, Change.Update update ->
                            Expect.equal
                                (DecisionUpdateInput.values update |> Map.ofList)
                                (Map.ofList
                                    [
                                        BusinessField.PaymentDecisionDate, Some ""
                                        BusinessField.PayableAmount, Some "10.00"
                                        BusinessField.PayableCurrency, Some "EUR"
                                    ])
                                "Authored date and complete money update survive"
                        | _ -> failtest "Correction selection changed during assembly"
                    | _ -> failtest "Correction task must assemble CORRECT")
        ]
