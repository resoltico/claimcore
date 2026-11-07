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
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.ApplicationFixtures


let internal ct = CancellationToken.None

let internal decision: DecisionInput =
    (ClaimCore.TestSupport.Inputs.decisionFrom
        [
            BusinessField.PaymentDecisionDate, ("2026-08-15")
            BusinessField.PayableAmount, ("750.00")
            BusinessField.PayableCurrency, ("EUR")
        ])

let internal keep: AuthoredCorrection =
    {
        Registration = Amendment.Keep
        Decision = Change.Keep
        Payment = PaymentChange.Keep
    }

let internal correction value = (AuthoredCaseAct.Correct value)

let internal payment =
    (AuthoredCaseAct.RecordPayment(
        ClaimCore.TestSupport.Inputs.paymentFrom [ BusinessField.PaymentDate, ("2026-08-20") ]
    ))

let internal decided = (AuthoredCaseAct.Decide decision)
let internal closed = (AuthoredCaseAct.Bare BareAct.Close)
let internal voided = (AuthoredCaseAct.Void "Synthetic reason")

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

    let reply: Outcome<Presented<CaseReadModel>> = Outcome.Rejected reason
    let encoded = Codec.encode Api.caseGet.Output reply

    Expect.equal
        (Codec.decode Limits.MaximumResponseBytes Api.caseGet.Output encoded)
        (Ok reply)
        "Every request-produced refusal crosses the typed Wire boundary"

    use document = JsonDocument.Parse(encoded.AsMemory())

    let token =
        document.RootElement
            .GetProperty("outcome")
            .GetProperty("data")
            .GetProperty("code")
            .GetString()

    Expect.equal
        token
        (identifier)
        "The public diagnostic token agrees with the request-producing Application refusal"

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
    | _ -> failtest "The request must produce a public refusal."

let internal queryRefused =
    function
    | Outcome.Rejected reason -> diagnostic reason
    | _ -> failtest "The query must produce a public refusal."

let internal execute actor pending =
    (Service.execute
        (actor: ServiceContext)
        (ClaimCore.TestSupport.Inputs.caseOperation pending)
        (ct))

let internal opening fixture values =
    { openRequest fixture 10 "REACH-1" with
        Act = (AuthoredCaseAct.Open values)
    }

let internal textRequests fixture () =
    [
        opening
            fixture
            (ClaimCore.TestSupport.Inputs.registrationWith
                [ BusinessField.ClaimantName, ("") ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.Inputs.registrationWith
                [ BusinessField.ClaimantName, (String(char 0xD800, 1)) ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.Inputs.registrationWith
                [ BusinessField.ClaimantName, (" Synthetic") ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.Inputs.registrationWith
                [ BusinessField.ClaimantName, (String('x', 201)) ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.Inputs.registrationWith
                [ BusinessField.ClaimantName, ("Synthetic\nParty") ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.Inputs.registrationWith
                [ BusinessField.ClaimantName, ("Synthetic​Party") ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.Inputs.registrationWith
                [ BusinessField.InsurerName, ("") ]
                (registration))
    ]

let internal registrationRequests fixture () =
    [
        opening
            fixture
            (ClaimCore.TestSupport.Inputs.registrationWith
                [ BusinessField.IncidentDate, ("not-a-date") ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.Inputs.registrationWith
                [ BusinessField.IncidentNotificationDate, ("not-a-date") ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.Inputs.registrationWith
                [ BusinessField.IncidentDate, ("2026-08-05") ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.Inputs.registrationWith
                [
                    BusinessField.IncidentDate, ("2027-01-01")
                    BusinessField.IncidentNotificationDate, ("2027-01-02")
                ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.Inputs.registrationWith
                [ BusinessField.IncidentCountry, ("Latvia") ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.Inputs.registrationWith
                [ BusinessField.ClaimedAmount, ("-1") ]
                (registration))
        opening
            fixture
            (ClaimCore.TestSupport.Inputs.registrationWith
                [ BusinessField.ClaimedCurrency, ("Euro") ]
                (registration))
    ]

let internal controlRequests fixture () =
    [
        openRequest fixture 10 "bad reference"
        { openRequest fixture 10 "REACH-1" with
            OperationId = Guid.Empty
        }
        { openRequest fixture 10 "REACH-1" with
            ExpectedRevision = -1L
        }
    ]

let internal progressRequests fixture () =
    [
        request
            fixture
            10
            "REACH-1"
            1L
            (AuthoredCaseAct.Decide(
                ClaimCore.TestSupport.Inputs.decisionWith
                    [ BusinessField.PaymentDecisionDate, ("bad") ]
                    (decision)
            ))
        request
            fixture
            10
            "REACH-1"
            1L
            (AuthoredCaseAct.Decide(
                ClaimCore.TestSupport.Inputs.decisionWith
                    [ BusinessField.PayableAmount, ("bad") ]
                    (decision)
            ))
        request
            fixture
            10
            "REACH-1"
            1L
            (AuthoredCaseAct.Decide(
                ClaimCore.TestSupport.Inputs.decisionWith
                    [ BusinessField.PayableCurrency, ("bad") ]
                    (decision)
            ))
        request
            fixture
            10
            "REACH-1"
            1L
            (AuthoredCaseAct.RecordPayment(
                ClaimCore.TestSupport.Inputs.paymentFrom [ BusinessField.PaymentDate, ("bad") ]
            ))
        actRequest fixture 10 "REACH-1" 1L ((AuthoredCaseAct.Void ""))
    ]

let internal scalarRequests fixture () =
    textRequests fixture ()
    @ registrationRequests fixture ()
    @ controlRequests fixture ()
    @ [
        actRequest
            fixture
            10
            "REACH-DESCRIPTION"
            0L
            (AuthoredCaseAct.Open(
                Inputs.registrationWith [ BusinessField.IncidentDescription, "" ] registration
            ))
    ]

let internal workScalarRequests fixture () =
    let work operation revision responsible summary =
        OperationInput.Work
            {
                OperationId = id fixture operation
                Target = CaseSelector.Reference "REACH-WORK"
                ExpectedRevision = revision
                Act =
                    WorkAct.UpdateWork
                        {
                            Responsible = responsible
                            Summary = summary
                        }
            }

    let reminder operation act =
        OperationInput.Reminder
            {
                OperationId = id fixture operation
                Target = CaseSelector.Reference "REACH-WORK"
                Act = act
            }

    [
        work 500 0L (WorkValue.Set "invalid") WorkValue.Keep
        work 501 0L WorkValue.Keep (WorkValue.Set "")
        work 502 0L WorkValue.Keep WorkValue.Keep
        work 503 -1L WorkValue.Keep (WorkValue.Set "A summary")
        reminder 504 (AuthoredReminderAct.Complete(Guid.Empty, 1L))
        reminder 505 (AuthoredReminderAct.Complete(id fixture 700, -1L))
        reminder 506 (AuthoredReminderAct.Create { Action = ""; Due = "invalid" })
        reminder
            507
            (AuthoredReminderAct.Create
                {
                    Action = String('a', 201)
                    Due = "2027-01-01"
                })
        work 508 0L WorkValue.Keep (WorkValue.Set(String('a', 601)))
    ]
