module ClaimCore.Tests.TransitionOracle

open ClaimCore.Contract

open ClaimCore.Domain

// This oracle covers the fixed synthetic vocabulary in TransitionPropertyTests, not arbitrary drafts.
let private activeEligible (fields: CaseFields) =
    let decided = fields.Decision.IsSome
    let paid = fields.Payment.IsSome

    function
    | CaseAct.Close -> true
    | CaseAct.AmendRegistration _ -> not decided
    | CaseAct.Decide _ -> not paid
    | CaseAct.WithdrawDecision -> decided && not paid
    | CaseAct.RecordPayment _ ->
        decided
        && not paid
        && (fields.Decision |> Option.map _.PayableAmount) <> Some "0"
    | CaseAct.ClearPayment -> paid
    | _ -> false

let private eligible (fields: CaseFields) act =
    match act with
    | CaseAct.Open _ -> false
    | CaseAct.Reopen -> fields.Status = CaseStatus.Closed
    | CaseAct.Correct _ -> fields.Status = CaseStatus.Closed || fields.Decision.IsSome
    | _ when fields.Status = CaseStatus.Closed -> false
    | _ -> activeEligible fields act

let private registration (value: Registration) (fields: CaseFields) =
    { fields with
        Registration = { value with ClaimedAmount = "1000" }
    }

let private correctedRegistration fields =
    function
    | RegistrationGroup.Replace value -> registration value fields
    | RegistrationGroup.Keep -> fields

let private changed fields act =
    match act with
    | CaseAct.AmendRegistration value -> registration value fields
    | CaseAct.Correct value -> correctedRegistration fields value.Registration
    | CaseAct.Decide value ->
        { fields with
            Decision =
                Some
                    { value with
                        PayableAmount = if value.PayableAmount = "0" then "0" else "750"
                    }
        }
    | CaseAct.WithdrawDecision -> { fields with Decision = None }
    | CaseAct.RecordPayment payment -> { fields with Payment = Some payment }
    | CaseAct.ClearPayment -> { fields with Payment = None }
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

let private business before fields stale act =
    let nextFields = changed fields act

    let noOpCorrection =
        match act with
        | CaseAct.Correct _ -> nextFields = fields
        | _ -> false

    if stale || not (eligible fields act) || noOpCorrection then
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
        | CaseView.Live(fields, Disposition.Active), act -> business before fields stale act
        | CaseView.Live(_, Disposition.Voided), _ -> None

let matches expected (actual: Result<CaseSnapshot, Refusal>) =
    match expected, actual with
    | Some view, Ok observed -> view = observed
    | None, Error _ -> true
    | _ -> false
