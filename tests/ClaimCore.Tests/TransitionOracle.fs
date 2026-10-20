module ClaimCore.Tests.TransitionOracle

open ClaimCore.Catalog

open ClaimCore.Domain

// This oracle covers the fixed synthetic vocabulary in TransitionPropertyTests, not arbitrary drafts.
let private eligible (fields: CaseFields) command =
    let decided = fields.PaymentDecisionDate.IsSome
    let paid = fields.PaymentDate.IsSome

    match command with
    | BusinessCommand.Open _ -> false
    | BusinessCommand.Reopen -> fields.Status = CaseStatus.Closed
    | BusinessCommand.CorrectCase _ -> fields.Status = CaseStatus.Closed || decided
    | _ when fields.Status = CaseStatus.Closed -> false
    | BusinessCommand.Close -> true
    | BusinessCommand.AmendRegistration _ -> not decided
    | BusinessCommand.Decide _ -> not paid
    | BusinessCommand.WithdrawDecision -> decided && not paid
    | BusinessCommand.RecordPayment _ -> decided && not paid && fields.PayableAmount <> Some "0"
    | BusinessCommand.ClearPayment -> paid

let private registration (value: RegistrationInput) (fields: CaseFields) =
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
    | BusinessCommand.AmendRegistration value -> registration value fields
    | BusinessCommand.CorrectCase value ->
        match value.Registration with
        | RegistrationCorrection.Replace value -> registration value fields
        | RegistrationCorrection.Keep -> fields
    | BusinessCommand.Decide value ->
        { fields with
            PaymentDecisionDate = Some value.PaymentDecisionDate
            PayableAmount = Some(if value.PayableAmount = "0" then "0" else "750")
            PayableCurrency = Some value.PayableCurrency
        }
    | BusinessCommand.WithdrawDecision ->
        { fields with
            PaymentDecisionDate = None
            PayableAmount = None
            PayableCurrency = None
        }
    | BusinessCommand.RecordPayment date -> { fields with PaymentDate = Some date }
    | BusinessCommand.ClearPayment -> { fields with PaymentDate = None }
    | BusinessCommand.Close ->
        { fields with
            Status = CaseStatus.Closed
        }
    | BusinessCommand.Reopen ->
        { fields with
            Status = CaseStatus.Opened
        }
    | BusinessCommand.Open _ -> fields

let private business before fields stale command =
    let nextFields = changed fields command

    let noOpCorrection =
        match command with
        | BusinessCommand.CorrectCase _ -> nextFields = fields
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
        | CaseView.Live(fields, Disposition.Active), CaseAct.Business command ->
            business before fields stale command
        | CaseView.Live(_, Disposition.Voided), CaseAct.Business _ -> None
        | CaseView.Live(fields, Disposition.Active), CaseAct.Stewardship(StewardshipAct.Void _) ->
            changed (CaseView.Live(fields, Disposition.Voided))
        | CaseView.Live(fields, Disposition.Voided), CaseAct.Stewardship(StewardshipAct.Reinstate _) ->
            changed (CaseView.Live(fields, Disposition.Active))
        | CaseView.Live _, CaseAct.Stewardship StewardshipAct.Erase -> changed CaseView.Erased
        | _ -> None

let matches expected (actual: Result<CaseSnapshot, CaseRefusal>) =
    match expected, actual with
    | Some view, Ok observed -> view = observed
    | None, Error _ -> true
    | _ -> false
