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
    Guid.Parse(sprintf "00000000-0000-7000-8000-%012d" number)

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
    let context =
        match act, current |> Option.map Case.snapshot with
        | CaseAct.RecordPayment _, Some parent ->
            CasePaymentContext.Recording(parent.CaseId, parent.Revision, false)
        | _ -> CasePaymentContext.NoneRequired

    Case.parse
        {
            OperationId = id operation
            CaseReference = "WORK-PARTIAL"
            ExpectedRevision = revision
            Act = act
        }
    |> Result.mapError CaseDecisionError.Refused
    |> Result.bind (fun command -> Case.decide today context command current)
    |> Result.map (fun decision -> Case.ofAccepted decision.After |> required)

let private opened () =
    run 1 0L (CaseAct.Open registration) None |> required

let private fields record =
    (Case.snapshot record).State |> CaseState.fields |> Option.get

let private patch: RegistrationUpdateInput =
    (RegistrationUpdateInput.create [] |> Result.defaultWith failtest)

let private correction decision : AuthoredCorrection =
    {
        Registration = Amendment.Keep
        Decision = decision
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
        PaymentInput.create
            [
                BusinessField.PaymentDate, "2026-01-05"
                BusinessField.PaidAmount, "800"
                BusinessField.PaidCurrency, "EUR"
            ]
        |> required

    let decided = run 2 1L (CaseAct.Decide decision) (Some(opened ())) |> required
    run 3 2L (CaseAct.RecordPayment payment) (Some decided) |> required

[<Tests>]
let tests =
    testList
        "typed work and partial updates"
        [
            testCase
                "work summary parsing and application preserve independent optional values"
                (fun () ->
                    let input: CaseWorkInput =
                        {
                            Responsible = WorkValue.Keep
                            Summary = WorkValue.Set "Current\nhandling context"
                        }

                    let update = CaseWorkInput.parse input |> required
                    let changed = CaseWork.apply update CaseWork.empty
                    Expect.equal changed.Responsible None "Summary does not assign"

                    Expect.equal
                        (changed.Summary |> Option.map HandlingSummary.format)
                        (Some "Current\nhandling context")
                        "Paragraph summary"

                    let clear: CaseWorkInput =
                        {
                            Responsible = WorkValue.Keep
                            Summary = WorkValue.Clear
                        }

                    Expect.equal
                        (CaseWork.apply (CaseWorkInput.parse clear |> required) changed)
                        CaseWork.empty
                        "Explicit clear"

                    Expect.isError
                        (CaseWorkInput.parse
                            {
                                Responsible = WorkValue.Keep
                                Summary = WorkValue.Keep
                            })
                        "No empty work input"

                    Expect.equal
                        (CaseDue.at
                            today
                            Lifecycle.Active
                            (Some CaseStatus.Closed)
                            (Some(date "2026-10-01")))
                        CaseDue.Inactive
                        "Inactive parent classification")
            testCase
                "one-field amendment preserves every unrelated value and refuses an empty update"
                (fun () ->
                    let initial = opened ()

                    let changed =
                        run
                            4
                            1L
                            (CaseAct.AmendRegistration(
                                RegistrationUpdateInput.replace
                                    [ BusinessField.ClaimantName, Some "Changed claimant" ]
                                    (patch)
                                |> Result.defaultWith failtest
                            ))
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
                            (CaseAct.AmendRegistration(
                                RegistrationUpdateInput.replace
                                    [ BusinessField.InsurerName, Some "Another insurer" ]
                                    (patch)
                                |> Result.defaultWith failtest
                            ))
                            (Some changed))
                        (Error(
                            CaseDecisionError.Refused(
                                Refusal.revisionConflict (Revision.create 2L |> Option.get)
                            )
                        ))
                        "Stale intent refuses")
            testCase
                "decision corrections retain payment entries without a payment acknowledgement"
                (fun () ->
                    let before = paid ()

                    let update: DecisionUpdateInput =
                        {
                            Date = Some "2026-01-03"
                            Payable = None
                        }

                    let changed =
                        run 5 3L (CaseAct.Correct(correction (Change.Update update))) (Some before)
                        |> required

                    Expect.equal
                        ((fields changed).Finance.PaymentDates
                         |> Option.map (_.Earliest >> CalendarDate.format))
                        (Some "2026-01-05")
                        "A correction cannot change a payment date"

                    Expect.equal
                        ((fields changed).Decision |> Option.map (Decision.payable >> Money.format))
                        (Some "800")
                        "Untouched money retained"

                    Expect.equal
                        (run 6 4L (CaseAct.Correct(correction Change.Keep)) (Some changed))
                        (Error(
                            CaseDecisionError.Refused(Refusal.Case CaseRefusal.CorrectionNoChanges)
                        ))
                        "Keeping both groups cannot create a revision")
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
                            (CaseAct.Correct(correction (Change.Update update)))
                            (Some(paid ()))
                    with
                    | Error(CaseDecisionError.Refused(Refusal.InvalidInput issues)) ->
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
                                    Profile = None
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
                    "No human-only capability")


        ]
