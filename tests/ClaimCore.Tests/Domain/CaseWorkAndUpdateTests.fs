module ClaimCore.Tests.CaseWorkAndUpdateTests

open System
open Expecto
open ClaimCore.Domain

let private required =
    function
    | Ok value -> value
    | Error issue -> failtestf "%A" issue

let private date value = CalendarDate.parse value |> required

let private id number =
    Guid.Parse(sprintf "00000000-0000-4000-8000-%012d" number)

let private today = DateOnly(2026, 11, 1)

let private registration =
    RegistrationInput.create
        [
            BusinessField.IncidentDate, "2026-01-01"
            BusinessField.IncidentNotificationDate, "2026-01-02"
            BusinessField.IncidentCountry, "LV"
            BusinessField.ClaimantName, "Original claimant"
            BusinessField.InsurerName, "Original insurer"
            BusinessField.ClaimedAmount, "1200.50"
            BusinessField.ClaimedCurrency, "EUR"
        ]
    |> required

let private run operation revision act current =
    Case.parse
        {
            OperationId = id operation
            CaseReference = "WORK-PARTIAL"
            ExpectedRevision = revision
            Act = act
        }
    |> Result.bind (fun command -> Case.decide today command current)

let private opened () =
    run 1 0L (CaseAct.Open registration) None |> required

let private fields record =
    (Case.snapshot record).State |> CaseState.fields |> Option.get

let private patch: RegistrationUpdateInput =
    {
        IncidentDate = None
        NotificationDate = None
        Country = None
        Claimant = None
        Insurer = None
        Claimed = None
    }

let private correction decision payment : AuthoredCorrection =
    {
        Registration = Amendment.Keep
        Decision = decision
        Payment = payment
    }

let private paid () =
    let decision =
        DecisionInput.create
            [
                BusinessField.PaymentDecisionDate, "2026-01-04"
                BusinessField.PayableAmount, "800"
                BusinessField.PayableCurrency, "EUR"
            ]
        |> required

    let payment =
        PaymentInput.create [ BusinessField.PaymentDate, "2026-01-05" ] |> required

    let decided = run 2 1L (CaseAct.Decide decision) (Some(opened ())) |> required
    run 3 2L (CaseAct.RecordPayment payment) (Some decided) |> required

[<Tests>]
let tests =
    testList
        "typed work and partial updates"
        [
            testCase
                "open has no assignment; tomorrow and unassigned follow-up are planned work"
                (fun () ->
                    let initial = opened ()

                    Expect.equal
                        (fields initial).Work
                        { Responsible = None; FollowUp = None }
                        "No invented work"

                    let changed =
                        run
                            4
                            1L
                            (CaseAct.UpdateWork
                                {
                                    Responsible = WorkValue.Keep
                                    FollowUp = WorkValue.Set "2026-11-02"
                                })
                            (Some initial)
                        |> required

                    Expect.equal
                        (fields changed).Work
                        {
                            Responsible = None
                            FollowUp = Some(date "2026-11-02")
                        }
                        "Future and unassigned are independent"

                    Expect.equal
                        ((Case.snapshot changed).Revision |> Revision.value)
                        2L
                        "One ordinary revision"

                    Expect.equal
                        (run
                            5
                            2L
                            (CaseAct.UpdateWork
                                {
                                    Responsible = WorkValue.Keep
                                    FollowUp = WorkValue.Set "2026-11-02"
                                })
                            (Some changed))
                        (Error(Refusal.Case CaseRefusal.WorkNoChanges))
                        "No unchanged revision"

                    let closed = run 6 2L (CaseAct.Bare BareAct.Close) (Some changed) |> required
                    Expect.equal (fields closed).Work (fields changed).Work "Close retains work"

                    Expect.equal
                        (CaseDue.at
                            today
                            Lifecycle.Active
                            (Some CaseStatus.Closed)
                            (fields closed).Work)
                        CaseDue.Inactive
                        "Closed work cannot be overdue")
            testCase
                "one-field amendment preserves every unrelated value and refuses an empty update"
                (fun () ->
                    let initial = opened ()

                    let changed =
                        run
                            4
                            1L
                            (CaseAct.AmendRegistration
                                { patch with
                                    Claimant = Some "Changed claimant"
                                })
                            (Some initial)
                        |> required

                    Expect.equal
                        (PartyName.format (Registration.claimant (fields changed).Registration))
                        "Changed claimant"
                        "Only asserted name"

                    Expect.equal
                        (Registration.insurer (fields changed).Registration)
                        (Registration.insurer (fields initial).Registration)
                        "Insurer retained"

                    Expect.equal
                        (Money.format (Registration.claimed (fields changed).Registration))
                        "1200.5"
                        "Complete original money retained"

                    Expect.isError
                        (run 5 2L (CaseAct.AmendRegistration patch) (Some changed))
                        "Empty is not a revision"

                    Expect.equal
                        ((Case.snapshot changed).Revision |> Revision.value)
                        2L
                        "No implicit rebase"

                    Expect.equal
                        (run
                            6
                            1L
                            (CaseAct.AmendRegistration
                                { patch with
                                    Insurer = Some "Another insurer"
                                })
                            (Some changed))
                        (Error(Refusal.RevisionConflict(Revision.create 2L |> Option.get)))
                        "Stale intent refuses")
            testCase
                "paid correction needs explicit payment intent; reaffirm keeps the actual date"
                (fun () ->
                    let before = paid ()

                    let update: DecisionUpdateInput =
                        {
                            Date = Some "2026-01-03"
                            Payable = None
                        }

                    Expect.equal
                        (run
                            4
                            3L
                            (CaseAct.Correct(correction (Change.Update update) PaymentChange.Keep))
                            (Some before))
                        (Error(
                            Refusal.invalidInput (
                                InputTarget.Group RecordGroup.Payment,
                                Violation.PaymentAcknowledgementRequired
                            )
                        ))
                        "No implied acknowledgement"

                    let changed =
                        run
                            5
                            3L
                            (CaseAct.Correct(
                                correction (Change.Update update) PaymentChange.Reaffirm
                            ))
                            (Some before)
                        |> required

                    Expect.equal
                        ((fields changed).Payment
                         |> Option.map (Payment.date >> CalendarDate.format))
                        (Some "2026-01-05")
                        "Reaffirm cannot invent a date"

                    Expect.equal
                        ((fields changed).Decision |> Option.map (Decision.payable >> Money.format))
                        (Some "800")
                        "Untouched money retained"

                    Expect.equal
                        (run
                            6
                            4L
                            (CaseAct.Correct(correction Change.Keep PaymentChange.Reaffirm))
                            (Some changed))
                        (Error(Refusal.Case CaseRefusal.CorrectionNoChanges))
                        "Reaffirm alone cannot create a revision")
            testCase
                "chronology targets an asserted decision against its untouched payment with context"
                (fun () ->
                    let update: DecisionUpdateInput =
                        {
                            Date = Some "2026-01-06"
                            Payable = None
                        }

                    match
                        run
                            4
                            3L
                            (CaseAct.Correct(
                                correction (Change.Update update) PaymentChange.Reaffirm
                            ))
                            (Some(paid ()))
                    with
                    | Error(Refusal.InvalidInput issues) ->
                        let found = InputIssues.toList issues
                        Expect.equal (List.length found) 1 "One relation, one issue"

                        Expect.equal
                            found.Head.Target
                            (InputTarget.Field BusinessField.PaymentDecisionDate)
                            "The retained payment is not offered as the asserted error"

                        Expect.equal
                            found.Head.RelatedDate
                            (Some
                                {
                                    Field = BusinessField.PaymentDate
                                    Value = date "2026-01-05"
                                })
                            "Exact counterpart context"
                    | other -> failtestf "Expected targeted chronology: %A" other)
            testCase "human capability does not invalidate service CASE_HANDLER" (fun () ->
                let principal = PrincipalKey.service "https://issuer.example/" "worker" |> required

                let view =
                    { AuthorityView.empty with
                        Actor =
                            Some
                                {
                                    ActorId = ActorId.create (id 10) |> Option.get
                                    Principal = principal
                                    Enabled = true
                                }
                        Installation = [ Role.CaseHandler ]
                    }

                Expect.isTrue
                    (Authority.validActor principal view)
                    "Role remains valid for services"

                for capability in
                    [
                        Capability.ReadCase
                        Capability.Register
                        Capability.Decide
                        Capability.Close
                    ] do
                    Expect.isTrue
                        (Authority.can principal view capability Scope.Installation)
                        "Existing service right retained"

                Expect.isFalse
                    (Authority.can principal view Capability.ManageCaseWork Scope.Installation)
                    "No human-only capability"

                Expect.isFalse
                    (Authority.canAdvertise
                        principal
                        view
                        Scope.Installation
                        CaseActKind.UpdateWork)
                    "No work offer")
        ]
