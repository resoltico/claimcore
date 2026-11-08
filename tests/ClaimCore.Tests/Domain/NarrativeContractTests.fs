module ClaimCore.Tests.NarrativeContractTests

open System
open Expecto
open ClaimCore.Domain

let private required =
    function
    | Ok value -> value
    | Error issue -> failtestf "%A" issue

let private registration description =
    [
        BusinessField.IncidentDate, "2026-01-01"
        BusinessField.IncidentNotificationDate, "2026-01-02"
        BusinessField.IncidentCountry, "LV"
        BusinessField.ClaimantName, "Narrative claimant"
        BusinessField.InsurerName, "Narrative insurer"
        BusinessField.ClaimedAmount, "500.00"
        BusinessField.ClaimedCurrency, "EUR"
        yield!
            description
            |> Option.map (fun value -> BusinessField.IncidentDescription, value)
            |> Option.toList
    ]
    |> RegistrationInput.create
    |> required

let private run number revision act current =
    Case.parse
        {
            OperationId = Guid.Parse(sprintf "97000000-0000-7000-8000-%012d" number)
            CaseReference = "NARRATIVE-QA"
            ExpectedRevision = revision
            Act = act
        }
    |> Result.bind (fun command -> Case.decide (DateOnly(2026, 11, 2)) command current)

let private fields value =
    (Case.snapshot value).State |> CaseState.fields |> Option.get

let private description value =
    Registration.description (fields value).Registration
    |> Option.map IncidentDescription.format

let private patch choice : RegistrationUpdateInput =
    {
        IncidentDate = None
        NotificationDate = None
        Country = None
        Claimant = None
        Insurer = None
        Claimed = None
        IncidentDescription = choice
    }

let private opened description =
    run 1 0L (CaseAct.Open(registration description)) None |> required

let private paid () =
    let decision =
        DecisionInput.create
            [
                BusinessField.PaymentDecisionDate, "2026-01-03"
                BusinessField.PayableAmount, "300.25"
                BusinessField.PayableCurrency, "EUR"
            ]
        |> required

    let payment =
        PaymentInput.create [ BusinessField.PaymentDate, "2026-01-04" ] |> required

    let decided =
        run 2 1L (CaseAct.Decide decision) (Some(opened (Some "Original description")))
        |> required

    run 3 2L (CaseAct.RecordPayment payment) (Some decided) |> required

[<Tests>]
let tests =
    testList
        "incident narrative contracts"
        [
            testCase
                "OPEN retains seven required fields with optional eighth description"
                (fun () ->
                    Expect.equal (description (opened None)) None "Absence is not a fake sentence"
                    let text = "  first\n\nوصف الحادث 😀  "

                    Expect.equal
                        (description (opened (Some text)))
                        (Some text)
                        "Exact optional value"

                    let missingRequired =
                        RegistrationInput.values (registration (Some text))
                        |> List.filter (fst >> (<>) BusinessField.InsurerName)

                    Expect.isError
                        (RegistrationInput.create missingRequired)
                        "Optional text does not relax required insurer"

                    let duplicate =
                        RegistrationInput.values (registration (Some text))
                        @ [ BusinessField.IncidentDescription, "Duplicate" ]

                    Expect.isError
                        (RegistrationInput.create duplicate)
                        "At most one optional value")
            testCase
                "partial omission preserves and explicit clear removes without touching money"
                (fun () ->
                    let original = opened (Some "Original description")

                    let rename =
                        { patch WorkValue.Keep with
                            Claimant = Some "Renamed claimant"
                        }

                    let renamed =
                        run 2 1L (CaseAct.AmendRegistration rename) (Some original) |> required

                    Expect.equal
                        (description renamed)
                        (Some "Original description")
                        "Omitted optional member preserves"

                    let cleared =
                        run 3 2L (CaseAct.AmendRegistration(patch WorkValue.Clear)) (Some renamed)
                        |> required

                    Expect.equal (description cleared) None "Explicit clear"

                    Expect.equal
                        (Registration.claimed (fields cleared).Registration |> Money.value)
                        500M
                        "No money assertion"

                    let replacement = "Revised\nparagraph"

                    let recorded =
                        run
                            4
                            3L
                            (CaseAct.AmendRegistration(patch (WorkValue.Set replacement)))
                            (Some cleared)
                        |> required

                    Expect.equal (description recorded) (Some replacement) "Set after clear"

                    Expect.equal
                        (Revision.value (Case.snapshot recorded).Revision)
                        4L
                        "Only accepted fact edits advance revision")
            testCase
                "paid description-only correction preserves exact decision and payment without acknowledgement"
                (fun () ->
                    let before = paid ()

                    let corrected =
                        run
                            4
                            3L
                            (CaseAct.Correct
                                {
                                    Registration =
                                        Amendment.Update(
                                            patch (WorkValue.Set "Only the narrative changes")
                                        )
                                    Decision = Change.Keep
                                    Payment = PaymentChange.Keep
                                })
                            (Some before)
                        |> required

                    Expect.equal
                        (fields corrected).Progress
                        (fields before).Progress
                        "Paid facts untouched"

                    Expect.equal
                        (description corrected)
                        (Some "Only the narrative changes")
                        "Narrative accepted"

                    let decisionPatch: DecisionUpdateInput =
                        {
                            Date = Some "2026-01-03"
                            Payable = Some { Amount = "300.50"; Currency = "EUR" }
                        }

                    let refused =
                        run
                            5
                            4L
                            (CaseAct.Correct
                                {
                                    Registration = Amendment.Keep
                                    Decision = Change.Update decisionPatch
                                    Payment = PaymentChange.Keep
                                })
                            (Some corrected)

                    match refused with
                    | Error(Refusal.InvalidInput issues) ->
                        Expect.equal
                            (InputIssues.head issues).Violation
                            Violation.PaymentAcknowledgementRequired
                            "Actual decision edit still requires acknowledgement"
                    | other -> failtestf "Expected financial safeguard, got %A" other)
            testCase
                "invalid supplied text refuses without editing original accepted state"
                (fun () ->
                    let before = opened (Some "Original")

                    for value, expected in
                        [
                            "", Violation.TextRequired
                            "\n ", Violation.TextRequired
                            "line\r\nnext", Violation.ControlCharacters
                            String.replicate 601 "😀", Violation.TooLong 600
                        ] do
                        let result =
                            run
                                2
                                1L
                                (CaseAct.AmendRegistration(patch (WorkValue.Set value)))
                                (Some before)

                        match result with
                        | Error(Refusal.InvalidInput issues) ->
                            let issue = InputIssues.head issues

                            Expect.equal
                                issue.Target
                                (InputTarget.Field BusinessField.IncidentDescription)
                                "Owning field"

                            Expect.equal issue.Violation expected "Independent expected refusal"
                        | other -> failtestf "Expected narrative refusal, got %A" other

                    Expect.equal (description before) (Some "Original") "No accepted mutation"

                    Expect.equal
                        (Revision.value (Case.snapshot before).Revision)
                        1L
                        "No revision produced")
            testCase "ordinary narrative changes refuse on voided and erased parents" (fun () ->
                let before = opened (Some "Original")
                let voided = run 2 1L (CaseAct.Void "Not a valid claim") (Some before) |> required

                Expect.isError
                    (run
                        3
                        2L
                        (CaseAct.AmendRegistration(patch (WorkValue.Set "Replacement")))
                        (Some voided))
                    "Voided refuses"

                let erased = run 4 2L (CaseAct.Bare BareAct.Erase) (Some voided) |> required

                Expect.isError
                    (run
                        5
                        3L
                        (CaseAct.AmendRegistration(patch (WorkValue.Set "Replacement")))
                        (Some erased))
                    "Erased refuses")
        ]
