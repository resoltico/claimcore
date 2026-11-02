module ClaimCore.Tests.TransitionOracle

open Expecto
open ClaimCore.TestSupport
open ClaimCore.Protocol

open ClaimCore.Domain

// This oracle covers the fixed synthetic vocabulary in TransitionPropertyTests, not arbitrary drafts.
let private activeEligible (fields: CaseFields) =
    let decided = fields.Decision.IsSome
    let paid = fields.Payment.IsSome

    function
    | (AuthoredCaseAct.Bare BareAct.Close) -> true
    | AuthoredCaseAct.UpdateWork _ -> true
    | AuthoredCaseAct.AmendRegistration _ -> not decided
    | AuthoredCaseAct.Decide _ -> not paid
    | (AuthoredCaseAct.Bare BareAct.WithdrawDecision) -> decided && not paid
    | AuthoredCaseAct.RecordPayment _ ->
        decided
        && not paid
        && (fields.Decision
            |> Option.map (fun value -> Money.value (Decision.payable (value))))
           <> Some 0M
    | (AuthoredCaseAct.Bare BareAct.ClearPayment) -> paid
    | _ -> false

let private eligible (fields: CaseFields) act =
    match act with
    | AuthoredCaseAct.Open _ -> false
    | (AuthoredCaseAct.Bare BareAct.Reopen) -> fields.Status = CaseStatus.Closed
    | AuthoredCaseAct.Correct _ -> fields.Status = CaseStatus.Closed || fields.Decision.IsSome
    | _ when fields.Status = CaseStatus.Closed -> false
    | _ -> activeEligible fields act

/// Positive witnesses cover the independent inventory; correction witnesses replace registration
/// only, so this oracle's synthetic state model does not invent arbitrary correction payloads.
let samples =
    let registration =
        Inputs.registrationWith
            [
                BusinessField.ClaimantName, "Changed Party"
                BusinessField.ClaimedAmount, "1000"
            ]
            Inputs.exampleRegistration

    let decision =
        Inputs.decisionWith [ BusinessField.PayableAmount, "750" ] Inputs.decision

    Inputs.caseActs registration decision Inputs.payment "Entry error"
    |> List.map (fun (act, token) ->
        match act with
        | AuthoredCaseAct.Correct _ ->
            AuthoredCaseAct.Correct
                {
                    Registration = Amendment.Update(Inputs.registrationUpdate registration)
                    Decision = Change.Keep
                    Payment = PaymentChange.Keep
                },
            token
        | _ -> act, token)

let allows state act =
    match state, act with
    | None, AuthoredCaseAct.Open _ -> true
    | None, _
    | Some CaseState.Erased, _ -> false
    | Some(CaseState.Active _), AuthoredCaseAct.Void _ -> true
    | Some(CaseState.Voided _), AuthoredCaseAct.Reinstate _ -> true
    | Some(CaseState.Active _ | CaseState.Voided _), AuthoredCaseAct.Bare BareAct.Erase -> true
    | Some(CaseState.Active fields), act -> eligible fields act
    | Some(CaseState.Voided _), _ -> false

let offered state =
    samples
    |> List.choose (fun (act, token) -> if allows (Some state) act then Some token else None)

// The fixed synthetic witnesses assert all registration fields. This literal boundary adapter
// is independent of the product partial merge and does not model arbitrary omitted fields.
let private registration (value: RegistrationUpdateInput) (fields: CaseFields) =
    let money = Option.get value.Claimed

    let complete =
        RegistrationInput.create
            [
                BusinessField.IncidentDate, Option.get value.IncidentDate
                BusinessField.IncidentNotificationDate, Option.get value.NotificationDate
                BusinessField.IncidentCountry, Option.get value.Country
                BusinessField.ClaimantName, Option.get value.Claimant
                BusinessField.InsurerName, Option.get value.Insurer
                BusinessField.ClaimedAmount, money.Amount
                BusinessField.ClaimedCurrency, money.Currency
            ]
        |> Result.defaultWith failtest

    { fields with
        Registration = CaseFixtures.registration complete
    }

let private correctedRegistration fields =
    function
    | Amendment.Update value -> registration value fields
    | Amendment.Keep -> fields

let private decidedProgress value =
    let amount =
        if DecisionInput.value BusinessField.PayableAmount value = "0" then
            "0"
        else
            "750"

    Inputs.decisionWith [ BusinessField.PayableAmount, amount ] value
    |> CaseFixtures.decision
    |> PaymentProgress.Decided

// This exhaustive fixed-witness effect table contains no nested decisions or admission policy.
// fsharplint:disable-next-line CyclomaticComplexity
let private changed (fields: CaseFields) act =
    match act with
    | AuthoredCaseAct.UpdateWork _ ->
        { fields with
            Work =
                {
                    Responsible = None
                    FollowUp = Some(CaseFixtures.date "2026-09-01")
                }
        }
    | AuthoredCaseAct.AmendRegistration value -> registration value fields
    | AuthoredCaseAct.Correct value -> correctedRegistration fields value.Registration
    | AuthoredCaseAct.Decide value ->
        { fields with
            Progress = decidedProgress value
        }
    | (AuthoredCaseAct.Bare BareAct.WithdrawDecision) ->
        { fields with
            Progress = PaymentProgress.Undecided
        }
    | AuthoredCaseAct.RecordPayment payment ->
        { fields with
            Progress =
                PaymentProgress.Paid(Option.get fields.Decision, (CaseFixtures.payment payment))
        }
    | (AuthoredCaseAct.Bare BareAct.ClearPayment) ->
        { fields with
            Progress = PaymentProgress.Decided(Option.get fields.Decision)
        }
    | (AuthoredCaseAct.Bare BareAct.Close) ->
        { fields with
            Status = CaseStatus.Closed
        }
    | (AuthoredCaseAct.Bare BareAct.Reopen) ->
        { fields with
            Status = CaseStatus.Opened
        }
    | AuthoredCaseAct.Open _
    | AuthoredCaseAct.Void _
    | AuthoredCaseAct.Reinstate _
    | (AuthoredCaseAct.Bare BareAct.Erase) -> fields

let private business (before: CaseSnapshot) fields stale act =
    if stale || not (eligible fields act) then
        None
    else
        let nextFields = changed fields act

        let noOpCorrection =
            match act with
            | AuthoredCaseAct.UpdateWork _
            | AuthoredCaseAct.Correct _ -> nextFields = fields
            | _ -> false

        if noOpCorrection then
            None
        else
            Some
                { before with
                    Revision = CaseFixtures.revision (Revision.value before.Revision + 1L)
                    State = CaseState.Active nextFields
                }

let advance (before: CaseSnapshot) stale act =
    if stale then
        None
    else
        let changed view =
            Some
                { before with
                    Revision = CaseFixtures.revision (Revision.value before.Revision + 1L)
                    State = view
                }

        match before.State, act with
        | CaseState.Erased, _ -> None
        | CaseState.Active(fields), AuthoredCaseAct.Void _ -> changed (CaseState.Voided(fields))
        | CaseState.Voided(fields), AuthoredCaseAct.Reinstate _ ->
            changed (CaseState.Active(fields))
        | (CaseState.Active _ | CaseState.Voided _), (AuthoredCaseAct.Bare BareAct.Erase) ->
            changed CaseState.Erased
        | _, (AuthoredCaseAct.Void _ | AuthoredCaseAct.Reinstate _) -> None
        | CaseState.Active(fields), act -> business before fields stale act
        | CaseState.Voided(_), _ -> None

let matches expected (actual: Result<CaseSnapshot, Refusal>) =
    match expected, actual with
    | Some view, Ok observed -> view = observed
    | None, Error _ -> true
    | _ -> false
