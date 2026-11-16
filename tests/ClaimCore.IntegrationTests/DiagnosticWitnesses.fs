module ClaimCore.IntegrationTests.DiagnosticWitnesses

open ClaimCore.Wire
open ClaimCore.Presentation

open ClaimCore.TestSupport

open ClaimCore.Json

open System
open System.Threading
open System.Text.Json
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.ApplicationFixtures


let internal ct = CancellationToken.None

let internal decision: DecisionInput =
    (ClaimCore.TestSupport.DomainInputs.decisionFrom
        [
            BusinessField.PaymentDecisionDate, ("2026-08-15")
            BusinessField.PayableAmount, ("750.00")
            BusinessField.PayableCurrency, ("EUR")
        ])

let internal baseline =
    lazy
        (let path =
            System.IO.Path.Combine(
                ClaimCore.TestSupport.RepositoryRoot.find (),
                "tests/fixtures/refusal-baseline.json"
            )

         use document = JsonDocument.Parse(System.IO.File.ReadAllBytes path)

         document.RootElement.GetProperty("observations").EnumerateArray()
         |> Seq.collect (fun row ->
             let code = row.GetProperty("code").GetString() |> nonNull
             let action = row.GetProperty("action").GetString() |> nonNull

             if code = "INVALID_INPUT" then
                 seq {
                     yield (code, None), action

                     for issue in row.GetProperty("issues").EnumerateArray() do
                         yield
                             (issue.GetProperty("code").GetString() |> nonNull,
                              Some(issue.GetProperty("field").GetString() |> nonNull)),
                             action
                 }
             else
                 Seq.singleton (
                     (code, row.GetProperty("target").GetString() |> Option.ofObj),
                     action
                 ))
         |> Map.ofSeq)

let internal baselinePolicy code target action =
    match Map.tryFind (code, target) baseline.Value with
    | Some expected -> Expect.equal (string action) expected "Captured repair action is preserved."
    | None ->
        failtestf "Request diagnostic is absent from the independent baseline: %s %A" code target

let internal diagnostic reason =
    let identifier = DiagnosticCodecs.code reason

    let reply = RepresentationReply.Data(Outcome<CaseReadModel>.Rejected reason)
    let encoded = Codec.encode Api.caseGet.Output reply

    Expect.equal
        (Codec.decode Limits.MaximumResponseBytes Api.caseGet.Output encoded)
        (Ok reply)
        "Every request-produced refusal crosses the typed Wire boundary"

    use document = JsonDocument.Parse(encoded.AsMemory())

    let token =
        document.RootElement
            .GetProperty("outcome")
            .GetProperty("rejection")
            .GetProperty("code")
            .GetString()

    Expect.equal
        token
        (identifier)
        "The public diagnostic token agrees with the request-producing core refusal"

    match reason with
    | Refusal.InvalidInput issues ->
        baselinePolicy "INVALID_INPUT" None RecommendedAction.CorrectInput

        ("INVALID_INPUT", None)
        :: (InputIssues.toList issues
            |> List.map (fun issue ->
                let code, _ = DiagnosticCodecs.describeIssue issue
                let target = Some(InputTarget.token issue.Target)
                baselinePolicy code target RecommendedAction.CorrectInput
                code, target))
    | reason ->
        let target =
            match reason with
            | Refusal.InvalidCursor -> Some(InputTarget.token InputTarget.Cursor)
            | Refusal.OperationIdOutOfWindow _ -> Some(InputTarget.token InputTarget.OperationId)
            | _ -> None

        baselinePolicy identifier target reason.Action
        [ identifier, target ]

let internal refused (reply: ExecuteReply) =
    match reply.Outcome with
    | ExecuteResult.Rejected reason -> diagnostic reason
    | other -> failtestf "The request must produce a public refusal: %A" other

let internal queryRefused =
    function
    | Outcome.Rejected reason -> diagnostic reason
    | _ -> failtest "The query must produce a public refusal."

let internal execute actor pending =
    (Service.executeSubmission (actor: ServiceContext) ((pending)) (ct))

let internal opening fixture values =
    request
        fixture
        10
        "REACH-1"
        0L
        "case.open"
        (Submissions.fields (RegistrationInput.values values))

let internal textRequests fixture () =
    [
        opening
            fixture
            (ClaimCore.TestSupport.DomainInputs.registrationWith
                [ BusinessField.ClaimantName, ("") ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.DomainInputs.registrationWith
                [ BusinessField.ClaimantName, (String(char 0xD800, 1)) ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.DomainInputs.registrationWith
                [ BusinessField.ClaimantName, (" Synthetic") ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.DomainInputs.registrationWith
                [ BusinessField.ClaimantName, (String('x', 201)) ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.DomainInputs.registrationWith
                [ BusinessField.ClaimantName, ("Synthetic\nParty") ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.DomainInputs.registrationWith
                [ BusinessField.ClaimantName, ("Synthetic​Party") ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.DomainInputs.registrationWith
                [ BusinessField.InsurerName, ("") ]
                (registration))
    ]

let internal registrationRequests fixture () =
    [
        opening
            fixture
            (ClaimCore.TestSupport.DomainInputs.registrationWith
                [ BusinessField.IncidentDate, ("not-a-date") ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.DomainInputs.registrationWith
                [ BusinessField.IncidentNotificationDate, ("not-a-date") ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.DomainInputs.registrationWith
                [ BusinessField.IncidentDate, ("2026-08-05") ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.DomainInputs.registrationWith
                [
                    BusinessField.IncidentDate, ("2027-01-01")
                    BusinessField.IncidentNotificationDate, ("2027-01-02")
                ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.DomainInputs.registrationWith
                [ BusinessField.IncidentCountry, ("Latvia") ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.DomainInputs.registrationWith
                [ BusinessField.ClaimedAmount, ("-1") ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.DomainInputs.registrationWith
                [ BusinessField.ClaimedCurrency, ("Euro") ]
                (registration))
    ]

let internal controlRequests fixture () =
    [
        openRequest fixture 10 "bad reference"
        ClaimCore.TestSupport.Submissions.withOperationId
            (Guid.Empty)
            (openRequest fixture 10 "REACH-1")
        Submissions.createRaw
            "case.open"
            (id fixture 10)
            (OperationBinding.CaseNew -1L)
            (Submissions.fields (RegistrationInput.values registration)
             |> Map.add
                 (Submissions.field BusinessField.CaseReference)
                 (SubmissionValue.Text "REACH-1"))
    ]

let internal progressRequests fixture () =
    [
        request
            (fixture)
            (10)
            ("REACH-1")
            (1L)
            "case.decide"
            (ClaimCore.TestSupport.Submissions.fields (
                DecisionInput.values (
                    (ClaimCore.TestSupport.DomainInputs.decisionWith
                        [ BusinessField.PaymentDecisionDate, ("bad") ]
                        (decision))
                )
            ))
        request
            (fixture)
            (10)
            ("REACH-1")
            (1L)
            "case.decide"
            (ClaimCore.TestSupport.Submissions.fields (
                DecisionInput.values (
                    (ClaimCore.TestSupport.DomainInputs.decisionWith
                        [ BusinessField.PayableAmount, ("bad") ]
                        (decision))
                )
            ))
        request
            (fixture)
            (10)
            ("REACH-1")
            (1L)
            "case.decide"
            (ClaimCore.TestSupport.Submissions.fields (
                DecisionInput.values (
                    (ClaimCore.TestSupport.DomainInputs.decisionWith
                        [ BusinessField.PayableCurrency, ("bad") ]
                        (decision))
                )
            ))
        request
            (fixture)
            (10)
            ("REACH-1")
            (1L)
            "case.recordPayment"
            (ClaimCore.TestSupport.Submissions.fields (
                PaymentInput.values (
                    (ClaimCore.TestSupport.DomainInputs.paymentFrom
                        [ BusinessField.PaymentDate, ("bad") ])
                )
            ))
        request
            (fixture)
            (10)
            ("REACH-1")
            (1L)
            "case.void"
            (Map.ofList
                [
                    TaskInputId.Target InputTarget.Reason,
                    ClaimCore.Application.Contracts.SubmissionValue.Text("")
                ])
    ]

let internal scalarRequests fixture () =
    textRequests fixture ()
    @ registrationRequests fixture ()
    @ controlRequests fixture ()
    @ [
        request
            (fixture)
            (10)
            ("REACH-DESCRIPTION")
            (0L)
            "case.open"
            (ClaimCore.TestSupport.Submissions.fields (
                RegistrationInput.values (
                    (DomainInputs.registrationWith
                        [ BusinessField.IncidentDescription, "" ]
                        registration)
                )
            ))
    ]

let internal workScalarRequests fixture () =
    let work operation revision responsible summary =
        (ClaimCore.TestSupport.Submissions.createRaw)
            "work.update"
            (id fixture operation)
            (ClaimCore.Application.Contracts.OperationBinding.Work(
                (CaseSelector.Reference "REACH-WORK"),
                (revision)
            ))
            (ClaimCore.TestSupport.Submissions.work (
                {
                    Responsible = responsible
                    Summary = summary
                }
            ))

    let reminder operation taskName selected values =
        Submissions.createRaw
            taskName
            (id fixture operation)
            (OperationBinding.Reminder((CaseSelector.Reference "REACH-WORK"), selected))
            values

    [
        work 500 0L (WorkValue.Set "invalid") WorkValue.Keep
        work 501 0L WorkValue.Keep (WorkValue.Set "")
        work 502 0L WorkValue.Keep WorkValue.Keep
        work 503 -1L WorkValue.Keep (WorkValue.Set "A summary")
        reminder (504) "reminder.complete" (Some((Guid.Empty), (1L))) (Map.empty)
        reminder (505) "reminder.complete" (Some((id fixture 700), (-1L))) (Map.empty)
        reminder
            (506)
            "reminder.create"
            (None)
            (ClaimCore.TestSupport.Submissions.reminderPlan ("") ("invalid"))
        reminder
            (507)
            "reminder.create"
            (None)
            (ClaimCore.TestSupport.Submissions.reminderPlan (String('a', 201)) ("2027-01-01"))
        work 508 0L WorkValue.Keep (WorkValue.Set(String('a', 601)))
    ]
