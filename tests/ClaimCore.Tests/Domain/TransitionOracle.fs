module ClaimCore.Tests.TransitionOracle

open Expecto
open ClaimCore.TestSupport
open ClaimCore.Wire
open ClaimCore.Presentation

open ClaimCore.Domain

// This oracle covers the fixed synthetic vocabulary in TransitionPropertyTests, not arbitrary drafts.
let private activeEligible (fields: CaseFields) =
    let hasDecision = fields.Decision.IsSome
    let paid = fields.Payments |> List.sumBy (PaymentEntry.amount >> Amount.value)

    function
    | AuthoredCaseAct.Bare BareAct.Close -> true
    | AuthoredCaseAct.AmendRegistration _ -> not hasDecision
    | AuthoredCaseAct.Decide input ->
        let payable =
            if DecisionInput.value BusinessField.PayableAmount input = "0" then
                0M
            else
                750M

        paid <= payable
    | AuthoredCaseAct.Bare BareAct.WithdrawDecision -> hasDecision && fields.Payments.IsEmpty
    | AuthoredCaseAct.RecordPayment _ ->
        hasDecision
        && fields.Payments.IsEmpty
        && (fields.Decision |> Option.map (Decision.payable >> Money.value)) = Some 750M
    | AuthoredCaseAct.RemovePayment id ->
        fields.Payments |> List.exists (PaymentEntry.id >> OperationId.value >> (=) id)
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
                },
            token
        | AuthoredCaseAct.RemovePayment _ ->
            AuthoredCaseAct.RemovePayment(System.Guid.Parse "20000000-0000-7000-8000-000000000001"),
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

    let complete =
        RegistrationInput.create
            [
                BusinessField.IncidentDate,
                (RegistrationUpdateInput.tryValue BusinessField.IncidentDate value |> Option.get)
                BusinessField.IncidentNotificationDate,
                (RegistrationUpdateInput.tryValue BusinessField.IncidentNotificationDate value
                 |> Option.get)
                BusinessField.IncidentCountry,
                (RegistrationUpdateInput.tryValue BusinessField.IncidentCountry value
                 |> Option.get)
                BusinessField.ClaimantName,
                (RegistrationUpdateInput.tryValue BusinessField.ClaimantName value |> Option.get)
                BusinessField.InsurerName,
                (RegistrationUpdateInput.tryValue BusinessField.InsurerName value |> Option.get)
                BusinessField.ClaimedAmount,
                (RegistrationUpdateInput.tryValue BusinessField.ClaimedAmount value |> Option.get)
                BusinessField.ClaimedCurrency,
                (RegistrationUpdateInput.tryValue BusinessField.ClaimedCurrency value
                 |> Option.get)
            ]
        |> Result.defaultWith failtest

    { fields with
        Registration = CaseFixtures.registration complete
    }

let private correctedRegistration fields =
    function
    | Amendment.Update value -> registration value fields
    | Amendment.Keep -> fields

let private progress decision entries =
    let payable = Money.value (Decision.payable decision)
    let sum = entries |> List.sumBy (PaymentEntry.amount >> Amount.value)

    if sum = payable then
        PaymentProgress.Paid(decision, entries)
    elif sum = 0M then
        PaymentProgress.Decided decision
    else
        PaymentProgress.PartlyPaid(decision, entries)

let private decidedProgress (fields: CaseFields) value =
    Inputs.decisionWith
        [
            BusinessField.PayableAmount,
            if DecisionInput.value BusinessField.PayableAmount value = "0" then
                "0"
            else
                "750"
        ]
        value
    |> CaseFixtures.decision
    |> fun decision -> progress decision fields.Payments

// This exhaustive fixed-witness effect table contains no nested decisions or admission policy.
let private changed (fields: CaseFields) act =
    match act with
    | AuthoredCaseAct.AmendRegistration value -> registration value fields
    | AuthoredCaseAct.Correct value -> correctedRegistration fields value.Registration
    | AuthoredCaseAct.Decide value ->
        { fields with
            Progress = decidedProgress fields value
        }
    | (AuthoredCaseAct.Bare BareAct.WithdrawDecision) ->
        { fields with
            Progress = PaymentProgress.Undecided
        }
    | AuthoredCaseAct.RecordPayment input ->
        let entry =
            PaymentEntry.create
                (System.Guid.Parse "20000000-0000-7000-8000-000000000001"
                 |> OperationId.create
                 |> Option.get)
                (PaymentInput.value BusinessField.PaymentDate input
                 |> CalendarDate.parse
                 |> Result.defaultWith (string >> failtest))
                (Amount.parse "750" |> Result.defaultWith (string >> failtest))
            |> Result.defaultWith failtest

        { fields with
            Progress = progress (Option.get fields.Decision) (fields.Payments @ [ entry ])
        }
    | AuthoredCaseAct.RemovePayment id ->
        { fields with
            Progress =
                progress
                    (Option.get fields.Decision)
                    (fields.Payments
                     |> List.filter (PaymentEntry.id >> OperationId.value >> ((<>) id)))
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
