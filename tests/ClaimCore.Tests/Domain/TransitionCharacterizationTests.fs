module ClaimCore.Tests.TransitionCharacterizationTests

open ClaimCore.TestSupport

open System
open Expecto
open ClaimCore.Protocol
open ClaimCore.Domain

let today = DateOnly(2026, 10, 26)

let registration claimant : RegistrationInput =
    { (Inputs.registrationOn "2026-09-01" "2026-09-02") with
        ClaimantName = claimant
        InsurerName = "Synthetic insurer"
        ClaimedAmount = "100"
    }

let decision amount : ClaimCore.Domain.DecisionInput =
    {
        PaymentDecisionDate = "2026-09-03"
        PayableAmount = amount
        PayableCurrency = "EUR"
    }

let payment: PaymentInput = { PaymentDate = "2026-09-04" }

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
        Reference = CaseFixtures.reference "DEMO-1"
        Revision = 3L
        State =
            state
                {
                    CaseReference = CaseFixtures.reference "DEMO-1"
                    Status = status
                    Registration = registration "Synthetic claimant" |> CaseFixtures.registration
                    Progress =
                        ClaimCore.TestSupport.CaseFixtures.progress
                            (decided |> Option.map CaseFixtures.decision)
                            ((if progress = Paid then
                                  Some(CaseFixtures.payment payment)
                              else
                                  None))
                }
    }

let erased: CaseSnapshot =
    {
        Reference = CaseFixtures.reference "DEMO-1"
        Revision = 3L
        State = CaseState.Erased
    }

let correction =
    AuthoredCaseAct.Correct
        {
            Registration = Amendment.Replace(registration "Corrected claimant")
            Decision = Change.Keep
            Payment = Change.Keep
        }

let acts =
    [
        "OPEN", AuthoredCaseAct.Open(registration "Synthetic claimant")
        "AMEND_REGISTRATION", AuthoredCaseAct.AmendRegistration(registration "Amended claimant")
        "CORRECT", correction
        "DECIDE", AuthoredCaseAct.Decide(decision "60")
        "WITHDRAW_DECISION", AuthoredCaseAct.WithdrawDecision
        "RECORD_PAYMENT", AuthoredCaseAct.RecordPayment payment
        "CLEAR_PAYMENT", AuthoredCaseAct.ClearPayment
        "CLOSE", AuthoredCaseAct.Close
        "REOPEN", AuthoredCaseAct.Reopen
        "VOID", AuthoredCaseAct.Void "Entry error"
        "REINSTATE", AuthoredCaseAct.Reinstate "Restored"
        "ERASE", AuthoredCaseAct.Erase
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
    | Error(Refusal.Case refusal) -> sprintf "%A" refusal
    | Error(Refusal.Authority refusal) -> sprintf "%A" refusal
    | Error(Refusal.Access refusal) -> sprintf "%A" refusal
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
            |> List.map CaseActKind.token

        for token, act in acts do
            let revision = if record.IsNone then 0L else 3L
            let decided = CaseFixtures.decide today (request act revision) record
            let stale = CaseFixtures.decide today (request act 2L) record
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
