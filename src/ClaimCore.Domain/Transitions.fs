namespace ClaimCore.Domain

open ClaimCore.Domain.ResultFlow

/// Eligibility carries the accepted sub-state required to perform the selected change.
[<RequireQualifiedAccess; NoComparison>]
type internal Applicable =
    private
    | Amending of Claim
    | Correcting of Claim
    | Deciding of Claim
    | Withdrawing of Claim
    | Paying of Claim * Decision
    | Unpaying of Claim * Decision
    | Closing of Claim
    | Reopening of Claim
    | Voiding of Claim
    | Reinstating of Claim
    | Erasing

module internal Transitions =
    let private amend claim =
        match claim.Progress with
        | PaymentProgress.Undecided -> Ok(Applicable.Amending claim)
        | PaymentProgress.Decided _
        | PaymentProgress.Paid _ -> Error Refusal.AmendmentRequiresUndecided

    let private correct claim =
        match claim.Progress with
        | PaymentProgress.Undecided -> Error Refusal.CorrectionRequiresExistingValue
        | PaymentProgress.Decided _
        | PaymentProgress.Paid _ -> Ok(Applicable.Correcting claim)

    let private decide claim =
        match claim.Progress with
        | PaymentProgress.Paid _ -> Error Refusal.DecisionAlreadyPaid
        | PaymentProgress.Undecided
        | PaymentProgress.Decided _ -> Ok(Applicable.Deciding claim)

    let private withdraw claim =
        match claim.Progress with
        | PaymentProgress.Undecided -> Error Refusal.DecisionRequired
        | PaymentProgress.Paid _ -> Error Refusal.DecisionAlreadyPaid
        | PaymentProgress.Decided _ -> Ok(Applicable.Withdrawing claim)

    let private pay claim =
        match claim.Progress with
        | PaymentProgress.Undecided -> Error Refusal.DecisionRequired
        | PaymentProgress.Paid _ -> Error Refusal.PaymentAlreadyRecorded
        | PaymentProgress.Decided decision when Money.value decision.PayableAmount = 0M ->
            Error Refusal.ZeroDecisionCannotBePaid
        | PaymentProgress.Decided decision -> Ok(Applicable.Paying(claim, decision))

    let private unpay claim =
        match claim.Progress with
        | PaymentProgress.Paid(decision, _) -> Ok(Applicable.Unpaying(claim, decision))
        | PaymentProgress.Undecided
        | PaymentProgress.Decided _ -> Error Refusal.PaymentNotRecorded

    let private opened claim =
        function
        | BusinessActKind.Open -> Error Refusal.CaseAlreadyExists
        | BusinessActKind.Reopen -> Error Refusal.AlreadyOpened
        | BusinessActKind.Close -> Ok(Applicable.Closing claim)
        | BusinessActKind.AmendRegistration -> amend claim
        | BusinessActKind.Correct -> correct claim
        | BusinessActKind.Decide -> decide claim
        | BusinessActKind.WithdrawDecision -> withdraw claim
        | BusinessActKind.RecordPayment -> pay claim
        | BusinessActKind.ClearPayment -> unpay claim

    let private business claim kind =
        match claim.Status, kind with
        | CaseStatus.Closed, BusinessActKind.Open -> Error Refusal.CaseAlreadyExists
        | CaseStatus.Closed, BusinessActKind.Reopen -> Ok(Applicable.Reopening claim)
        | CaseStatus.Closed, BusinessActKind.Close -> Error Refusal.AlreadyClosed
        | CaseStatus.Closed, BusinessActKind.Correct -> Ok(Applicable.Correcting claim)
        | CaseStatus.Closed, _ -> Error Refusal.CaseClosed
        | CaseStatus.Opened, _ -> opened claim kind

    let applicable state kind =
        match state, kind with
        | CaseState.Erased, _ -> Error Refusal.CaseErased
        | (CaseState.Active _ | CaseState.Voided _), CaseActKind.Business BusinessActKind.Open ->
            Error Refusal.CaseAlreadyExists
        | CaseState.Voided _, CaseActKind.Stewardship StewardshipActKind.Void ->
            Error Refusal.AlreadyVoided
        | CaseState.Voided _, CaseActKind.Business _ -> Error Refusal.CaseVoided
        | CaseState.Active _, CaseActKind.Stewardship StewardshipActKind.Reinstate ->
            Error Refusal.NotVoided
        | CaseState.Active claim, CaseActKind.Stewardship StewardshipActKind.Void ->
            Ok(Applicable.Voiding claim)
        | CaseState.Voided claim, CaseActKind.Stewardship StewardshipActKind.Reinstate ->
            Ok(Applicable.Reinstating claim)
        | (CaseState.Active _ | CaseState.Voided _),
          CaseActKind.Stewardship StewardshipActKind.Erase -> Ok Applicable.Erasing
        | CaseState.Active claim, CaseActKind.Business kind -> business claim kind

    let private businessChange before after =
        Some before, after |> Result.map CaseState.Active

    /// The proof supplies the decision for payment changes; no state shape is recovered by a partial operation.
    let private change evidence payload =
        match evidence, payload with
        | Applicable.Amending claim, CaseAct.Business(BusinessAct.AmendRegistration facts) ->
            businessChange claim (Ok { claim with Facts = facts })
        | Applicable.Correcting claim, CaseAct.Business(BusinessAct.Correct correction) ->
            CaseCorrections.apply claim.Facts claim.Progress correction
            |> Result.map (fun (facts, progress) ->
                { claim with
                    Facts = facts
                    Progress = progress
                })
            |> businessChange claim
        | Applicable.Deciding claim, CaseAct.Business(BusinessAct.Decide decision) ->
            businessChange
                claim
                (Ok
                    { claim with
                        Progress = PaymentProgress.Decided decision
                    })
        | Applicable.Withdrawing claim, CaseAct.Business BusinessAct.WithdrawDecision ->
            businessChange
                claim
                (Ok
                    { claim with
                        Progress = PaymentProgress.Undecided
                    })
        | Applicable.Paying(claim, decision), CaseAct.Business(BusinessAct.RecordPayment date) ->
            businessChange
                claim
                (Ok
                    { claim with
                        Progress = PaymentProgress.Paid(decision, date)
                    })
        | Applicable.Unpaying(claim, decision), CaseAct.Business BusinessAct.ClearPayment ->
            businessChange
                claim
                (Ok
                    { claim with
                        Progress = PaymentProgress.Decided decision
                    })
        | Applicable.Closing claim, CaseAct.Business BusinessAct.Close ->
            businessChange
                claim
                (Ok
                    { claim with
                        Status = CaseStatus.Closed
                    })
        | Applicable.Reopening claim, CaseAct.Business BusinessAct.Reopen ->
            businessChange
                claim
                (Ok
                    { claim with
                        Status = CaseStatus.Opened
                    })
        | Applicable.Voiding claim, CaseAct.Void reason ->
            Some claim,
            Reason.admit reason
            |> Result.map (fun _ -> CaseState.Voided claim)
            |> Result.mapError (fun violation ->
                Refusal.InvalidInput(InputTarget.Reason, violation))
        | Applicable.Reinstating claim, CaseAct.Reinstate reason ->
            Some claim,
            Reason.admit reason
            |> Result.map (fun _ -> CaseState.Active claim)
            |> Result.mapError (fun violation ->
                Refusal.InvalidInput(InputTarget.Reason, violation))
        | Applicable.Erasing, CaseAct.Erase ->
            // Erasure consumes no business payload and performs no new scalar admission.
            None, Ok CaseState.Erased
        | _ -> None, Error Refusal.ActNotPermitted

    let apply today evidence payload =
        let before, changed = change evidence payload

        changed
        |> Result.bind (fun next ->
            match before, payload, next with
            | Some before, CaseAct.Business _, CaseState.Active after ->
                result {
                    do! StateValidation.progress after.Facts after.Progress

                    do!
                        StateValidation.newlyAssertedDates
                            today
                            before.Facts
                            before.Progress
                            after.Facts
                            after.Progress

                    do! Validation.assertedText (Some before.Facts) after.Facts
                    return next
                }
            | _ -> Ok next)
