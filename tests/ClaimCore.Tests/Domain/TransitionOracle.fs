module ClaimCore.Tests.TransitionOracle

open ClaimCore.TestSupport
open ClaimCore.Protocol

open ClaimCore.Domain

// This oracle covers the fixed synthetic vocabulary in TransitionPropertyTests, not arbitrary drafts.
let private activeEligible (fields: CaseFields) =
    let decided = fields.Decision.IsSome
    let paid = fields.Payment.IsSome

    function
    | (AuthoredCaseAct.Bare BareAct.Close) -> true
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

let private registration (value: RegistrationInput) (fields: CaseFields) =
    { fields with
        Registration =
            CaseFixtures.registration (
                ClaimCore.TestSupport.Inputs.registrationWith
                    [ BusinessField.ClaimedAmount, ("1000") ]
                    (value)
            )
    }

let private correctedRegistration fields =
    function
    | Amendment.Replace value -> registration value fields
    | Amendment.Keep -> fields

let private changed fields act =
    match act with
    | AuthoredCaseAct.AmendRegistration value -> registration value fields
    | AuthoredCaseAct.Correct value -> correctedRegistration fields value.Registration
    | AuthoredCaseAct.Decide value ->
        { fields with
            Progress =
                PaymentProgress.Decided(
                    CaseFixtures.decision (
                        ClaimCore.TestSupport.Inputs.decisionWith
                            [
                                BusinessField.PayableAmount,
                                (if
                                     (DecisionInput.value BusinessField.PayableAmount (value)) = "0"
                                 then
                                     "0"
                                 else
                                     "750")
                            ]
                            (value)
                    )
                )
        }
    | (AuthoredCaseAct.Bare BareAct.WithdrawDecision) ->
        { fields with
            Progress = PaymentProgress.Undecided
        }
    | AuthoredCaseAct.RecordPayment payment ->
        { fields with
            Progress =
                PaymentProgress.Paid(
                    Option.get fields.Decision,
                    (Payment.date (CaseFixtures.payment payment))
                )
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
