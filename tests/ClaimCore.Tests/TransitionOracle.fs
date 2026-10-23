module ClaimCore.Tests.TransitionOracle

open ClaimCore.Contract

open ClaimCore.Domain

// This oracle covers the fixed synthetic vocabulary in TransitionPropertyTests, not arbitrary drafts.
let private eligible (fields: CaseFields) command =
    let decided = fields.PaymentDecisionDate.IsSome
    let paid = fields.PaymentDate.IsSome

    match command with
    | CaseAct.Open _ -> false
    | CaseAct.Reopen -> fields.Status = CaseStatus.Closed
    | CaseAct.CorrectCase _ -> fields.Status = CaseStatus.Closed || decided
    | _ when fields.Status = CaseStatus.Closed -> false
    | CaseAct.Close -> true
    | CaseAct.AmendRegistration _ -> not decided
    | CaseAct.Decide _ -> not paid
    | CaseAct.WithdrawDecision -> decided && not paid
    | CaseAct.RecordPayment _ -> decided && not paid && fields.PayableAmount <> Some "0"
    | CaseAct.ClearPayment -> paid
    | CaseAct.Void _
    | CaseAct.Reinstate _
    | CaseAct.Erase -> false

let private registration (value: Registration) (fields: CaseFields) =
    { fields with
        IncidentDate = value.IncidentDate
        IncidentNotificationDate = value.IncidentNotificationDate
        IncidentCountry = value.IncidentCountry
        ClaimantName = value.ClaimantName
        InsurerName = value.InsurerName
        ClaimedAmount = "1000"
        ClaimedCurrency = value.ClaimedCurrency
    }

let private changed fields command =
    match command with
    | CaseAct.AmendRegistration value -> registration value fields
    | CaseAct.CorrectCase value ->
        match value.Registration with
        | RegistrationGroup.Replace value -> registration value fields
        | RegistrationGroup.Keep -> fields
    | CaseAct.Decide value ->
        { fields with
            PaymentDecisionDate = Some value.PaymentDecisionDate
            PayableAmount = Some(if value.PayableAmount = "0" then "0" else "750")
            PayableCurrency = Some value.PayableCurrency
        }
    | CaseAct.WithdrawDecision ->
        { fields with
            PaymentDecisionDate = None
            PayableAmount = None
            PayableCurrency = None
        }
    | CaseAct.RecordPayment date ->
        { fields with
            PaymentDate = Some date.PaymentDate
        }
    | CaseAct.ClearPayment -> { fields with PaymentDate = None }
    | CaseAct.Close ->
        { fields with
            Status = CaseStatus.Closed
        }
    | CaseAct.Reopen ->
        { fields with
            Status = CaseStatus.Opened
        }
    | CaseAct.Open _
    | CaseAct.Void _
    | CaseAct.Reinstate _
    | CaseAct.Erase -> fields

let private business before fields stale command =
    let nextFields = changed fields command

    let noOpCorrection =
        match command with
        | CaseAct.CorrectCase _ -> nextFields = fields
        | _ -> false

    if stale || not (eligible fields command) || noOpCorrection then
        None
    else
        Some
            { before with
                Revision = before.Revision + 1L
                View = CaseView.Live(nextFields, Disposition.Active)
            }

let advance (before: CaseSnapshot) stale act =
    if stale then
        None
    else
        let changed view =
            Some
                { before with
                    Revision = before.Revision + 1L
                    View = view
                }

        match before.View, act with
        | CaseView.Erased, _ -> None
        | CaseView.Live(fields, Disposition.Active), CaseAct.Void _ ->
            changed (CaseView.Live(fields, Disposition.Voided))
        | CaseView.Live(fields, Disposition.Voided), CaseAct.Reinstate _ ->
            changed (CaseView.Live(fields, Disposition.Active))
        | CaseView.Live _, CaseAct.Erase -> changed CaseView.Erased
        | _, (CaseAct.Void _ | CaseAct.Reinstate _) -> None
        | CaseView.Live(fields, Disposition.Active), command -> business before fields stale command
        | CaseView.Live(_, Disposition.Voided), _ -> None

let matches expected (actual: Result<CaseSnapshot, Refusal>) =
    match expected, actual with
    | Some view, Ok observed -> view = observed
    | None, Error _ -> true
    | _ -> false
