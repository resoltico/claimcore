module ClaimCore.Tests.TransitionOracle

open ClaimCore.TestSupport
open ClaimCore.Contract

open ClaimCore.Domain

// This oracle covers the fixed synthetic vocabulary in TransitionPropertyTests, not arbitrary drafts.
let private activeEligible (fields: CaseFields) =
    let decided = fields.Decision.IsSome
    let paid = fields.Payment.IsSome

    function
    | UnvalidatedCaseAct.Close -> true
    | UnvalidatedCaseAct.AmendRegistration _ -> not decided
    | UnvalidatedCaseAct.Decide _ -> not paid
    | UnvalidatedCaseAct.WithdrawDecision -> decided && not paid
    | UnvalidatedCaseAct.RecordPayment _ ->
        decided
        && not paid
        && (fields.Decision |> Option.map (fun value -> Money.value value.PayableAmount))
           <> Some 0M
    | UnvalidatedCaseAct.ClearPayment -> paid
    | _ -> false

let private eligible (fields: CaseFields) act =
    match act with
    | UnvalidatedCaseAct.Open _ -> false
    | UnvalidatedCaseAct.Reopen -> fields.Status = CaseStatus.Closed
    | UnvalidatedCaseAct.Correct _ -> fields.Status = CaseStatus.Closed || fields.Decision.IsSome
    | _ when fields.Status = CaseStatus.Closed -> false
    | _ -> activeEligible fields act

let private registration (value: RegistrationInput) (fields: CaseFields) =
    { fields with
        Registration = CaseFixtures.registration { value with ClaimedAmount = "1000" }
    }

let private correctedRegistration fields =
    function
    | RegistrationGroup.Replace value -> registration value fields
    | RegistrationGroup.Keep -> fields

let private changed fields act =
    match act with
    | UnvalidatedCaseAct.AmendRegistration value -> registration value fields
    | UnvalidatedCaseAct.Correct value -> correctedRegistration fields value.Registration
    | UnvalidatedCaseAct.Decide value ->
        { fields with
            Decision =
                Some(
                    CaseFixtures.decision
                        { value with
                            PayableAmount = if value.PayableAmount = "0" then "0" else "750"
                        }
                )
        }
    | UnvalidatedCaseAct.WithdrawDecision -> { fields with Decision = None }
    | UnvalidatedCaseAct.RecordPayment payment ->
        { fields with
            Payment = Some(CaseFixtures.payment payment)
        }
    | UnvalidatedCaseAct.ClearPayment -> { fields with Payment = None }
    | UnvalidatedCaseAct.Close ->
        { fields with
            Status = CaseStatus.Closed
        }
    | UnvalidatedCaseAct.Reopen ->
        { fields with
            Status = CaseStatus.Opened
        }
    | UnvalidatedCaseAct.Open _
    | UnvalidatedCaseAct.Void _
    | UnvalidatedCaseAct.Reinstate _
    | UnvalidatedCaseAct.Erase -> fields

let private business (before: CaseSnapshot) fields stale act =
    let nextFields = changed fields act

    let noOpCorrection =
        match act with
        | UnvalidatedCaseAct.Correct _ -> nextFields = fields
        | _ -> false

    if stale || not (eligible fields act) || noOpCorrection then
        None
    else
        Some
            { before with
                Revision = before.Revision + 1L
                State = CaseState.Active(nextFields)
            }

let advance (before: CaseSnapshot) stale act =
    if stale then
        None
    else
        let changed view =
            Some
                { before with
                    Revision = before.Revision + 1L
                    State = view
                }

        match before.State, act with
        | CaseState.Erased, _ -> None
        | CaseState.Active(fields), UnvalidatedCaseAct.Void _ -> changed (CaseState.Voided(fields))
        | CaseState.Voided(fields), UnvalidatedCaseAct.Reinstate _ ->
            changed (CaseState.Active(fields))
        | (CaseState.Active _ | CaseState.Voided _), UnvalidatedCaseAct.Erase ->
            changed CaseState.Erased
        | _, (UnvalidatedCaseAct.Void _ | UnvalidatedCaseAct.Reinstate _) -> None
        | CaseState.Active(fields), act -> business before fields stale act
        | CaseState.Voided(_), _ -> None

let matches expected (actual: Result<CaseSnapshot, Refusal>) =
    match expected, actual with
    | Some view, Ok observed -> view = observed
    | None, Error _ -> true
    | _ -> false
