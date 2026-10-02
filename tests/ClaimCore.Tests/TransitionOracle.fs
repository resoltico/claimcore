module ClaimCore.Tests.TransitionOracle

open ClaimCore.Domain

// This oracle covers the fixed synthetic vocabulary in TransitionPropertyTests, not arbitrary drafts.
let private eligible (fields: CaseFields) command =
    let decided = fields.PaymentDecisionDate.IsSome
    let paid = fields.PaymentDate.IsSome

    match command with
    | Command.Open _ -> false
    | Command.Reopen -> fields.Status = CaseStatus.Closed
    | Command.CorrectCase _ -> fields.Status = CaseStatus.Closed || decided
    | _ when fields.Status = CaseStatus.Closed -> false
    | Command.Close -> true
    | Command.AmendRegistration _ -> not decided
    | Command.Decide _ -> not paid
    | Command.WithdrawDecision -> decided && not paid
    | Command.RecordPayment _ -> decided && not paid && fields.PayableAmount <> Some "0"
    | Command.ClearPayment -> paid

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
    | Command.AmendRegistration value -> registration value fields
    | Command.CorrectCase value ->
        match value.Registration with
        | RegistrationCorrection.Replace value -> registration value fields
        | RegistrationCorrection.Keep -> fields
    | Command.Decide value ->
        { fields with
            PaymentDecisionDate = Some value.PaymentDecisionDate
            PayableAmount = Some(if value.PayableAmount = "0" then "0" else "750")
            PayableCurrency = Some value.PayableCurrency
        }
    | Command.WithdrawDecision ->
        { fields with
            PaymentDecisionDate = None
            PayableAmount = None
            PayableCurrency = None
        }
    | Command.RecordPayment date -> { fields with PaymentDate = Some date }
    | Command.ClearPayment -> { fields with PaymentDate = None }
    | Command.Close ->
        { fields with
            Status = CaseStatus.Closed
        }
    | Command.Reopen ->
        { fields with
            Status = CaseStatus.Opened
        }
    | Command.Open _ -> fields

let advance (before: CaseView) stale command =
    let fields = changed before.Fields command

    let noOpCorrection =
        match command with
        | Command.CorrectCase _ -> fields = before.Fields
        | _ -> false

    if stale || not (eligible before.Fields command) || noOpCorrection then
        None
    else
        Some
            {
                Fields = fields
                Version = before.Version + 1L
            }

let matches expected (actual: Result<CaseView, DomainError>) =
    match expected, actual with
    | Some view, Ok observed -> view = observed
    | None, Error _ -> true
    | _ -> false
