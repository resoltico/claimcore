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
        match claim.Status, claim.Progress with
        | CaseStatus.Closed, _ -> Ok(Applicable.Correcting claim)
        | CaseStatus.Opened, PaymentProgress.Undecided ->
            Error Refusal.CorrectionRequiresExistingValue
        | CaseStatus.Opened, (PaymentProgress.Decided _ | PaymentProgress.Paid _) ->
            Ok(Applicable.Correcting claim)

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

    let private whenOpened decide claim =
        match claim.Status with
        | CaseStatus.Closed -> Error Refusal.CaseClosed
        | CaseStatus.Opened -> decide claim

    let private close claim =
        match claim.Status with
        | CaseStatus.Closed -> Error Refusal.AlreadyClosed
        | CaseStatus.Opened -> Ok(Applicable.Closing claim)

    let private reopen claim =
        match claim.Status with
        | CaseStatus.Closed -> Ok(Applicable.Reopening claim)
        | CaseStatus.Opened -> Error Refusal.AlreadyOpened

    let private active claim =
        function
        | CaseActKind.Open -> Error Refusal.CaseAlreadyExists
        | CaseActKind.AmendRegistration -> whenOpened amend claim
        | CaseActKind.Correct -> correct claim
        | CaseActKind.Decide -> whenOpened decide claim
        | CaseActKind.WithdrawDecision -> whenOpened withdraw claim
        | CaseActKind.RecordPayment -> whenOpened pay claim
        | CaseActKind.ClearPayment -> whenOpened unpay claim
        | CaseActKind.Close -> close claim
        | CaseActKind.Reopen -> reopen claim
        | CaseActKind.Void -> Ok(Applicable.Voiding claim)
        | CaseActKind.Reinstate -> Error Refusal.NotVoided
        | CaseActKind.Erase -> Ok Applicable.Erasing

    let private voided claim =
        function
        | CaseActKind.Open -> Error Refusal.CaseAlreadyExists
        | CaseActKind.Void -> Error Refusal.AlreadyVoided
        | CaseActKind.Reinstate -> Ok(Applicable.Reinstating claim)
        | CaseActKind.Erase -> Ok Applicable.Erasing
        | _ -> Error Refusal.CaseVoided

    let applicable state kind =
        match state with
        | CaseState.Erased -> Error Refusal.CaseErased
        | CaseState.Voided claim -> voided claim kind
        | CaseState.Active claim -> active claim kind

    let private businessChange before after =
        Some before, after |> Result.map CaseState.Active

    /// The proof supplies the decision for payment changes; no state shape is recovered by a partial operation.
    let private change evidence payload =
        match evidence, payload with
        | Applicable.Amending claim, CaseAct.AmendRegistration facts ->
            businessChange claim (Ok { claim with Facts = facts })
        | Applicable.Correcting claim, CaseAct.Correct correction ->
            CaseCorrections.apply claim.Facts claim.Progress correction
            |> Result.map (fun (facts, progress) ->
                { claim with
                    Facts = facts
                    Progress = progress
                })
            |> businessChange claim
        | Applicable.Deciding claim, CaseAct.Decide decision ->
            businessChange
                claim
                (Ok
                    { claim with
                        Progress = PaymentProgress.Decided decision
                    })
        | Applicable.Withdrawing claim, CaseAct.WithdrawDecision ->
            businessChange
                claim
                (Ok
                    { claim with
                        Progress = PaymentProgress.Undecided
                    })
        | Applicable.Paying(claim, decision), CaseAct.RecordPayment payment ->
            businessChange
                claim
                (Ok
                    { claim with
                        Progress = PaymentProgress.Paid(decision, payment.PaymentDate)
                    })
        | Applicable.Unpaying(claim, decision), CaseAct.ClearPayment ->
            businessChange
                claim
                (Ok
                    { claim with
                        Progress = PaymentProgress.Decided decision
                    })
        | Applicable.Closing claim, CaseAct.Close ->
            businessChange
                claim
                (Ok
                    { claim with
                        Status = CaseStatus.Closed
                    })
        | Applicable.Reopening claim, CaseAct.Reopen ->
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
            | Some before, act, CaseState.Active after when
                not (CaseActKind.isStewardship (CaseAct.kind act))
                ->
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
