module ClaimCore.Tests.CaseAvailabilityTests

open Expecto
open ClaimCore.Domain
open ClaimCore.TestSupport
open ClaimCore.Tests.Fixtures

[<Tests>]
let tests =
    testList
        "case action boundaries"
        [
            testCase "financial states expose the independently specified open-case acts" (fun () ->
                let zero =
                    opened ()
                    |> apply (
                        AuthoredCaseAct.Decide(
                            Inputs.decisionWith [ BusinessField.PayableAmount, "0" ] decision
                        )
                    )
                    |> accepted

                let partial =
                    decided ()
                    |> apply (
                        AuthoredCaseAct.RecordPayment(
                            Inputs.paymentFrom [ BusinessField.PaidAmount, "250" ]
                        )
                    )
                    |> accepted

                let common = [ CaseActKind.Close; CaseActKind.Void; CaseActKind.Erase ]

                let rows =
                    [
                        opened (), [ CaseActKind.AmendRegistration; CaseActKind.Decide ]
                        decided (),
                        [
                            CaseActKind.Correct
                            CaseActKind.Decide
                            CaseActKind.WithdrawDecision
                            CaseActKind.RecordPayment
                        ]
                        partial,
                        [
                            CaseActKind.Correct
                            CaseActKind.Decide
                            CaseActKind.RecordPayment
                            CaseActKind.RemovePayment
                        ]
                        paid (),
                        [ CaseActKind.Correct; CaseActKind.Decide; CaseActKind.RemovePayment ]
                        zero,
                        [ CaseActKind.Correct; CaseActKind.Decide; CaseActKind.WithdrawDecision ]
                    ]

                for record, financial in rows do
                    Expect.equal
                        (Case.eligibleActs record |> Set.ofList)
                        (common @ financial |> Set.ofList)
                        "Open-case availability follows entry state"

                    let closed = record |> apply (AuthoredCaseAct.Bare BareAct.Close) |> accepted

                    Expect.equal
                        (Case.eligibleActs closed |> Set.ofList)
                        (set[CaseActKind.Correct
                             CaseActKind.Reopen
                             CaseActKind.Void
                             CaseActKind.Erase])
                        "Closed-case boundary"

                    let voided = record |> apply (AuthoredCaseAct.Void "Entry error") |> accepted

                    Expect.equal
                        (Case.eligibleActs voided |> Set.ofList)
                        (set[CaseActKind.Reinstate
                             CaseActKind.Erase])
                        "Voided-case boundary"

                    let erased = record |> apply (AuthoredCaseAct.Bare BareAct.Erase) |> accepted
                    Expect.isEmpty (Case.eligibleActs erased) "Erasure is terminal")
            testCase
                "stale revisions precede state refusals except terminal erasure and an existing open"
                (fun () ->
                    let active = paid ()
                    let closed = active |> apply (AuthoredCaseAct.Bare BareAct.Close) |> accepted
                    let voided = active |> apply (AuthoredCaseAct.Void "Entry error") |> accepted

                    for before in [ active; closed; voided ] do
                        let request =
                            { request 0L (AuthoredCaseAct.Bare BareAct.Close) with
                                ExpectedRevision = 0L
                            }

                        match CaseFixtures.decide today Map.empty request (Some before) with
                        | Error(CaseDecisionError.Refused(Refusal.RevisionConflict conflict)) ->
                            Expect.equal
                                conflict.ActualRevision
                                (Case.snapshot before).Revision
                                "Conflict names the state used for the action"
                        | result -> failtestf "Expected revision conflict, got %A" result

                    let erased = active |> apply (AuthoredCaseAct.Bare BareAct.Erase) |> accepted

                    Expect.equal
                        (CaseFixtures.decide
                            today
                            Map.empty
                            (request 0L (AuthoredCaseAct.Bare BareAct.Close))
                            (Some erased))
                        (Error(
                            CaseDecisionError.Refused(
                                Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Erased)
                            )
                        ))
                        "Erasure does not reveal a mutable revision path"

                    Expect.equal
                        (CaseFixtures.decide
                            today
                            Map.empty
                            (request 0L (AuthoredCaseAct.Open registration))
                            (Some active))
                        (Error(
                            CaseDecisionError.Refused(Refusal.Case CaseRefusal.CaseAlreadyExists)
                        ))
                        "Open never replaces an existing case")
        ]
