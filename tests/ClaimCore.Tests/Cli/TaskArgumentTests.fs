module ClaimCore.Tests.TaskArgumentTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Cli
open ClaimCore.Wire

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
            testCase
                "payment task retains exact authored precision and explicit reviewed revision"
                (fun () ->
                    match
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
                    with
                    | OperationInput.Case value ->
                        Expect.equal value.OperationId (Guid.Parse operation) "Allocated identity"

                        Expect.equal
                            value.Target
                            (CaseSelector.Reference "TASK")
                            "Reference remains an authored selector"

                        Expect.equal value.ExpectedRevision 7L "Explicit revision"

                        match value.Act with
                        | CaseAct.RecordPayment payment ->
                            Expect.equal
                                (PaymentInput.values payment)
                                [
                                    BusinessField.PaymentDate, "2026-11-09"
                                    BusinessField.PaidAmount, "1.2300"
                                    BusinessField.PaidCurrency, "EUR"
                                ]
                                "No normalization before review or recording"
                        | _ -> failtest "Payment act required"
                    | _ -> failtest "Case command required")
            testCase
                "correction preserves separate keep clear and selected scalar intent"
                (fun () ->
                    match
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
                                "--claimant-name"
                                "Edited claimant"
                                "--decision"
                                "clear"
                            ]
                    with
                    | OperationInput.Case { Act = CaseAct.Correct value } ->
                        match value.Registration with
                        | Amendment.Update update ->
                            Expect.equal
                                (RegistrationUpdateInput.values update |> Map.ofList)
                                (Map.ofList
                                    [
                                        BusinessField.PolicyNumber, None
                                        BusinessField.ClaimantName, Some "Edited claimant"
                                    ])
                                "Omission is keep and explicit clear is retained"
                        | _ -> failtest "Registration update required"

                        Expect.equal value.Decision Change.Clear "Decision clear is independent"
                    | _ -> failtest "Correction required")
            testCase "compound authority roles retain submitted order and typed scopes" (fun () ->
                match
                    input
                        [
                            "authority"
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
                with
                | OperationInput.Authority { Act = AuthorityAct.Admit value } ->
                    Expect.equal value.Profile.DisplayName "Editable name" "Profile input"

                    Expect.equal
                        value.Grants
                        [
                            {
                                Role = Role.CaseReader
                                Scope = GrantTarget.CaseReference "CASE-A"
                            }
                            {
                                Role = Role.Auditor
                                Scope = GrantTarget.Installation
                            }
                        ]
                        "Submitted ordinal map follows this exact order"
                | _ -> failtest "Compound authority admission required")
            testCase
                "malformed metadata and unselected fields report usage without throwing or fabricating commands"
                (fun () ->
                    for args in
                        [
                            [
                                "authority"
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
                            [ "case"; "close"; "TASK"; "--expected-revision"; "-1" ]
                            [ "case"; "close"; "TASK"; "--expected-revision"; "9007199254740992" ]
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
                            "Usage failure before executable delivery")
        ]

[<Tests>]
let creationBasis =
    testCase "case creation never acquires an authored expected revision" (fun () ->
        let task = TaskId.Operation(ActKind.Case CaseActKind.Open)

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
                     | ClaimCore.Presentation.UsageError.UnknownOption("--expected-revision", _) ->
                         true
                     | _ -> false))
                "Supplying the retired override is a specific syntax refusal"
        | Ok _ -> failtest "An authored version cannot override creation")
