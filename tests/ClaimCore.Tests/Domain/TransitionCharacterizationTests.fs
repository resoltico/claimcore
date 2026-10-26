module ClaimCore.Tests.TransitionCharacterizationTests

open System
open Expecto
open ClaimCore.Contract
open ClaimCore.Domain

let today = DateOnly(2026, 10, 26)

let registration claimant : Registration =
    {
        IncidentDate = "2026-09-01"
        IncidentNotificationDate = "2026-09-02"
        IncidentCountry = "LV"
        ClaimantName = claimant
        InsurerName = "Synthetic insurer"
        ClaimedAmount = "100"
        ClaimedCurrency = "EUR"
    }

let decision amount : ClaimCore.Domain.Decision =
    {
        PaymentDecisionDate = "2026-09-03"
        PayableAmount = amount
        PayableCurrency = "EUR"
    }

let payment: Payment = { PaymentDate = "2026-09-04" }

type Progress =
    | Undecided
    | DecidedPositive
    | DecidedZero
    | Paid

let snapshot status progress state : CaseSnapshot =
    let decided =
        match progress with
        | Undecided -> None
        | DecidedZero -> Some(decision "0")
        | _ -> Some(decision "50")

    {
        Reference = "DEMO-1"
        Revision = 3L
        State =
            state
                {
                    CaseReference = "DEMO-1"
                    Status = status
                    Registration = registration "Synthetic claimant"
                    Decision = decided
                    Payment = (if progress = Paid then Some payment else None)
                }
    }

let erased: CaseSnapshot =
    {
        Reference = "DEMO-1"
        Revision = 3L
        State = CaseState.Erased
    }

let correction =
    CaseAct.Correct
        {
            Registration = RegistrationGroup.Replace(registration "Corrected claimant")
            Decision = DecisionGroup.Keep
            Payment = PaymentGroup.Keep
        }

let acts =
    [
        "OPEN", CaseAct.Open(registration "Synthetic claimant")
        "AMEND_REGISTRATION", CaseAct.AmendRegistration(registration "Amended claimant")
        "CORRECT", correction
        "DECIDE", CaseAct.Decide(decision "60")
        "WITHDRAW_DECISION", CaseAct.WithdrawDecision
        "RECORD_PAYMENT", CaseAct.RecordPayment payment
        "CLEAR_PAYMENT", CaseAct.ClearPayment
        "CLOSE", CaseAct.Close
        "REOPEN", CaseAct.Reopen
        "VOID", CaseAct.Void "Entry error"
        "REINSTATE", CaseAct.Reinstate "Restored"
        "ERASE", CaseAct.Erase
    ]

let request act revision : UnvalidatedCaseRequest =
    {
        OperationId = Guid.Parse "10000000-0000-4000-8000-000000000001"
        CaseReference = "DEMO-1"
        ExpectedRevision = revision
        Act = act
    }

let classes =
    [
        for status in [ CaseStatus.Opened; CaseStatus.Closed ] do
            for progress in [ Undecided; DecidedPositive; DecidedZero; Paid ] do
                for name, state in [ "Active", CaseState.Active; "Voided", CaseState.Voided ] do
                    sprintf "%A/%A/%s" status progress name, Some(snapshot status progress state)
        "ERASED", Some erased
        "ABSENT", None
    ]

let outcome =
    function
    | Ok(_: CaseRecord) -> "ACCEPTED"
    | Error refusal -> sprintf "%A" refusal |> fun text -> text.Replace("\n", " ")

let matrix () =
    let lines = ResizeArray<string>()
    lines.Add "class\tact\toffered\toutcome\tstaleRevisionOutcome"

    for name, current in classes do
        let record =
            current
            |> Option.map (fun value ->
                Case.ofAccepted value
                |> Result.defaultWith (fun issue -> failwithf "fixture %s: %A" name issue))

        let offered =
            record
            |> Option.map Case.eligibleActs
            |> Option.defaultValue []
            |> List.map CaseActKinds.token

        for token, act in acts do
            let revision = if record.IsNone then 0L else 3L
            let decided = Case.decide today (request act revision) record
            let stale = Case.decide today (request act 2L) record
            let isOffered = List.contains token offered

            lines.Add(
                sprintf "%s\t%s\t%b\t%s\t%s" name token isOffered (outcome decided) (outcome stale)
            )

    lines |> Seq.toArray

[<Tests>]
let tests =
    testCase
        "transition characterization preserves every reviewed lifecycle and refusal cell"
        (fun () ->
            let path =
                System.IO.Path.Combine(
                    ClaimCore.TestSupport.RepositoryRoot.find (),
                    "tests/fixtures/transition-matrix.tsv"
                )

            let expected = System.IO.File.ReadAllLines path
            let actual = matrix ()

            Expecto.Expect.equal
                actual.Length
                217
                "Header plus the independently reviewed 216 cells"

            Expecto.Expect.sequenceEqual
                actual
                expected
                "Offers, acceptance and stale-revision precedence match the reviewed baseline")
