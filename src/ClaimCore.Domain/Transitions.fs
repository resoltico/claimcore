namespace ClaimCore.Domain

open ClaimCore.Domain.ResultFlow

/// Eligibility carries the accepted sub-state required to perform the selected change.
[<RequireQualifiedAccess; NoComparison>]
type internal Applicable<'r, 'd, 'du, 'p, 'id, 's> =
    private
    | Amending of Claim * 'r
    | Correcting of Claim * Correction<'r, 'du>
    | Deciding of Claim * 'd
    | Withdrawing of Claim
    | Paying of Claim * Decision * 'p
    | Removing of Claim * Decision * 'id
    | Closing of Claim
    | Reopening of Claim
    | Voiding of Claim * 's
    | Reinstating of Claim * 's
    | Erasing of Lifecycle

module internal Transitions =
    let private notOffered (claim: Claim) =
        CaseRefusal.NotOffered(
            CaseHeadline.ofState (
                CaseState.Active(claim.Status, CaseProgress.ofFinancial claim.Finance)
            )
        )

    let private amend facts (claim: Claim) =
        if claim.Finance.Decision.IsNone then
            Ok(Applicable.Amending(claim, facts))
        else
            Error(notOffered claim)

    let private correct correction (claim: Claim) =
        if claim.Status = CaseStatus.Closed || claim.Finance.Decision.IsSome then
            Ok(Applicable.Correcting(claim, correction))
        else
            Error(notOffered claim)

    let private decide decision claim =
        Ok(Applicable.Deciding(claim, decision))

    let private withdraw (claim: Claim) =
        if claim.Finance.Decision.IsSome && claim.Finance.PaymentCount = 0L then
            Ok(Applicable.Withdrawing claim)
        else
            Error(notOffered claim)

    let private pay payment (claim: Claim) =
        match claim.Finance.Decision with
        | Some decision when
            Amount.value claim.Finance.PaidAmount < Money.value (Decision.payable decision)
            ->
            Ok(Applicable.Paying(claim, decision, payment))
        | _ -> Error(notOffered claim)

    let private remove id (claim: Claim) =
        match claim.Finance.Decision with
        | Some decision when claim.Finance.PaymentCount > 0L ->
            Ok(Applicable.Removing(claim, decision, id))
        | _ -> Error(notOffered claim)

    let private whenOpened decide claim =
        match claim.Status with
        | CaseStatus.Closed -> Error(notOffered claim)
        | CaseStatus.Opened -> decide claim

    let private close claim =
        match claim.Status with
        | CaseStatus.Closed -> Error(notOffered claim)
        | CaseStatus.Opened -> Ok(Applicable.Closing claim)

    let private reopen claim =
        match claim.Status with
        | CaseStatus.Closed -> Ok(Applicable.Reopening claim)
        | CaseStatus.Opened -> Error(notOffered claim)

    let private activeBusiness claim =
        function
        | CaseAct.Open _ -> Error CaseRefusal.CaseAlreadyExists
        | CaseAct.AmendRegistration facts -> whenOpened (amend facts) claim
        | CaseAct.Correct correction -> correct correction claim
        | CaseAct.Decide decision -> whenOpened (decide decision) claim
        | CaseAct.Bare BareAct.WithdrawDecision -> whenOpened withdraw claim
        | CaseAct.RecordPayment payment -> whenOpened (pay payment) claim
        | CaseAct.RemovePayment id -> whenOpened (remove id) claim
        | CaseAct.Bare BareAct.Close -> close claim
        | CaseAct.Bare BareAct.Reopen -> reopen claim
        | _ -> invalidOp "A stewardship act cannot enter business rules."

    let private activeStewardship claim =
        function
        | CaseAct.Void reason -> Ok(Applicable.Voiding(claim, reason))
        | CaseAct.Reinstate _ -> Error(notOffered claim)
        | CaseAct.Bare BareAct.Erase -> Ok(Applicable.Erasing Lifecycle.Active)
        | _ -> invalidOp "A business act cannot enter stewardship rules."

    let private active claim act =
        match (CaseActKind.describe (CaseAct.kind act)).Family with
        | ActFamily.Business -> activeBusiness claim act
        | ActFamily.Stewardship -> activeStewardship claim act

    let private voidedBusiness =
        function
        | CaseAct.Open _ -> Error CaseRefusal.CaseAlreadyExists
        | _ -> Error(CaseRefusal.NotOffered CaseHeadline.Voided)

    let private voidedStewardship claim =
        function
        | CaseAct.Void _ -> Error(CaseRefusal.NotOffered CaseHeadline.Voided)
        | CaseAct.Reinstate reason -> Ok(Applicable.Reinstating(claim, reason))
        | CaseAct.Bare BareAct.Erase -> Ok(Applicable.Erasing Lifecycle.Voided)
        | _ -> invalidOp "A business act cannot enter stewardship rules."

    let private voided claim act =
        match (CaseActKind.describe (CaseAct.kind act)).Family with
        | ActFamily.Business -> voidedBusiness act
        | ActFamily.Stewardship -> voidedStewardship claim act

    let applicable state act =
        match state with
        | CaseState.Erased -> Error(CaseRefusal.NotOffered CaseHeadline.Erased)
        | CaseState.Voided claim -> voided claim act
        | CaseState.Active claim -> active claim act

    let private stateAfter before act fields =
        Lifecycle.after before act
        |> Option.map (fun state -> CaseState.ofLifecycle state fields)
        |> Option.defaultWith (fun () ->
            invalidOp "Applicable evidence requires a structural lifecycle transition.")

    let private businessChange act before after =
        Some before, after |> Result.map (stateAfter Lifecycle.Active act)

    let private erased before =
        // Erasure carries only lifecycle evidence, never business payload.
        match Lifecycle.after before CaseActKind.Erase with
        | Some Lifecycle.Erased -> None, Ok CaseState.Erased
        | _ -> invalidOp "Applicable erasure evidence requires an erased result."

    /// The proof supplies the decision for payment changes; no state shape is recovered by a partial operation.
    let private change today paymentTransition evidence =
        match evidence with
        | Applicable.Amending(claim, update) ->
            RegistrationUpdate.apply update claim.Facts
            |> InputValidation.registrationMerge
            |> Result.map (fun facts -> { claim with Facts = facts })
            |> businessChange CaseActKind.AmendRegistration claim
        | Applicable.Correcting(claim, correction) ->
            CaseCorrections.apply today claim correction
            |> Result.map (fun (facts, finance) ->
                { claim with
                    Facts = facts
                    Finance = finance
                })
            |> businessChange CaseActKind.Correct claim
        | Applicable.Deciding(claim, decision) ->
            result {
                do! StateValidation.decisionChange claim.Finance (Some decision)

                do!
                    StateValidation.crossFinancial
                        (Decision.values decision |> List.map fst |> Set.ofList)
                        (Registration.incidentNotificationDate claim.Facts)
                        (Some decision)
                        claim.Finance

                let finance =
                    FinancialPosition.withDecision (Some decision) claim.Finance
                    |> Result.defaultWith (fun _ ->
                        invalidOp "Admitted decision retains a valid financial position.")

                return { claim with Finance = finance }
            }
            |> businessChange CaseActKind.Decide claim
        | Applicable.Withdrawing claim ->
            businessChange
                CaseActKind.WithdrawDecision
                claim
                (Ok
                    { claim with
                        Finance = FinancialPosition.undecided
                    })
        | Applicable.Paying(claim, decision, payment) ->
            let changed =
                match paymentTransition with
                | PaymentTransition.Recording true -> Error Refusal.IdempotencyConflict
                | PaymentTransition.Recording false ->
                    FinancialPosition.record today payment claim.Finance
                    |> Result.map (fun finance -> { claim with Finance = finance })
                    |> Result.mapError (function
                        | FinancialPositionIssue.CurrencyMismatch ->
                            Refusal.invalidInput (
                                InputTarget.Field BusinessField.PaidCurrency,
                                Violation.PaymentCurrencyMismatch
                            )
                        | FinancialPositionIssue.ExceedsPayable ->
                            Refusal.invalidInput (
                                InputTarget.Field BusinessField.PaidAmount,
                                Violation.PaymentExceedsPayable
                            )
                        | FinancialPositionIssue.FuturePayment ->
                            Refusal.invalidInput (
                                InputTarget.Field BusinessField.PaymentDate,
                                Violation.FutureDate
                            )
                        | FinancialPositionIssue.CountOutOfRange ->
                            Refusal.invalidInput (
                                InputTarget.Group RecordGroup.Payment,
                                Violation.PaymentCountLimit
                            )
                        | FinancialPositionIssue.PaymentBeforeDecision ->
                            InputValidation.chronology
                                BusinessField.PaymentDate
                                BusinessField.PaymentDecisionDate
                                (Decision.date decision)
                            |> function
                                | Error refusal -> refusal
                                | Ok _ -> invalidOp "Earlier payment requires chronology refusal."
                        | _ ->
                            invalidOp
                                "Validated payment and position require a determinate financial result.")
                | _ -> invalidOp "Payment recording requires its admitted identity context."

            businessChange CaseActKind.RecordPayment claim changed
        | Applicable.Removing(claim, _, _) ->
            let changed =
                match paymentTransition with
                | PaymentTransition.Removing None -> Error(Refusal.Case CaseRefusal.PaymentNotFound)
                | PaymentTransition.Removing(Some(_, finance)) ->
                    Ok { claim with Finance = finance }
                | _ -> invalidOp "Payment removal requires its admitted entry context."

            businessChange CaseActKind.RemovePayment claim changed
        | Applicable.Closing claim ->
            businessChange
                CaseActKind.Close
                claim
                (Ok
                    { claim with
                        Status = CaseStatus.Closed
                    })
        | Applicable.Reopening claim ->
            businessChange
                CaseActKind.Reopen
                claim
                (Ok
                    { claim with
                        Status = CaseStatus.Opened
                    })
        | Applicable.Voiding(claim, reason) ->
            Some claim,
            Reason.admit reason
            |> Result.map (fun _ -> stateAfter Lifecycle.Active CaseActKind.Void claim)
            |> Result.mapError (fun violation ->
                Refusal.invalidInput (InputTarget.Reason, violation))
        | Applicable.Reinstating(claim, reason) ->
            Some claim,
            Reason.admit reason
            |> Result.map (fun _ -> stateAfter Lifecycle.Voided CaseActKind.Reinstate claim)
            |> Result.mapError (fun violation ->
                Refusal.invalidInput (InputTarget.Reason, violation))
        | Applicable.Erasing before -> erased before

    let private admitBusiness today before changed =
        match before, changed with
        | Some before, Ok(CaseState.Active after) -> StateValidation.admit today (Some before) after
        | _ -> Ok()

    let apply today paymentTransition evidence =
        let before, changed = change today paymentTransition evidence

        let admission =
            match evidence with
            | Applicable.Correcting _ -> Ok()
            | Applicable.Voiding _
            | Applicable.Reinstating _
            | Applicable.Erasing _ -> Ok()
            | Applicable.Amending(current, update) ->
                StateValidation.assertionsAgainst
                    today
                    (Some current)
                    (RegistrationUpdate.values update)
            | Applicable.Paying(_, _, payment) ->
                StateValidation.assertionsAgainst today None (Payment.values payment)
            | Applicable.Deciding _
            | Applicable.Withdrawing _
            | Applicable.Removing _
            | Applicable.Closing _
            | Applicable.Reopening _ -> admitBusiness today before changed

        Admission.map2 (fun next () -> next) changed admission
        |> InputValidation.ordered
