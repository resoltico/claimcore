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
                CaseState.Active(claim.Status, CaseProgress.ofPayment claim.Progress)
            )
        )

    let private amend facts claim =
        match claim.Progress with
        | PaymentProgress.Undecided -> Ok(Applicable.Amending(claim, facts))
        | PaymentProgress.Decided _
        | PaymentProgress.PartlyPaid _
        | PaymentProgress.Paid _ -> Error(notOffered claim)

    let private correct correction claim =
        match claim.Status, claim.Progress with
        | CaseStatus.Closed, _ -> Ok(Applicable.Correcting(claim, correction))
        | CaseStatus.Opened, PaymentProgress.Undecided -> Error(notOffered claim)
        | CaseStatus.Opened,
          (PaymentProgress.Decided _ | PaymentProgress.PartlyPaid _ | PaymentProgress.Paid _) ->
            Ok(Applicable.Correcting(claim, correction))

    let private decide decision claim =
        Ok(Applicable.Deciding(claim, decision))

    let private withdraw claim =
        if
            (PaymentProgress.decision claim.Progress).IsSome
            && (PaymentProgress.payments claim.Progress).IsEmpty
        then
            Ok(Applicable.Withdrawing claim)
        else
            Error(notOffered claim)

    let private pay payment claim =
        match PaymentProgress.decision claim.Progress with
        | Some decision when
            (PaymentProgress.payments claim.Progress).Length < PaymentProgress.MaximumPayments
            && PaymentProgress.paidValue claim.Progress < Money.value (Decision.payable decision)
            ->
            Ok(Applicable.Paying(claim, decision, payment))
        | _ -> Error(notOffered claim)

    let private remove id claim =
        match PaymentProgress.decision claim.Progress with
        | Some decision when not (PaymentProgress.payments claim.Progress).IsEmpty ->
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
    let private change today operationId evidence =
        match evidence with
        | Applicable.Amending(claim, update) ->
            RegistrationUpdate.apply update claim.Facts
            |> InputValidation.registrationMerge
            |> Result.map (fun facts -> { claim with Facts = facts })
            |> businessChange CaseActKind.AmendRegistration claim
        | Applicable.Correcting(claim, correction) ->
            CaseCorrections.apply today claim correction
            |> Result.map (fun (facts, progress) ->
                { claim with
                    Facts = facts
                    Progress = progress
                })
            |> businessChange CaseActKind.Correct claim
        | Applicable.Deciding(claim, decision) ->
            result {
                do! StateValidation.decisionChange claim.Progress (Some decision)

                do!
                    StateValidation.crossProgress
                        (Decision.values decision |> List.map fst |> Set.ofList)
                        (Registration.incidentNotificationDate claim.Facts)
                        (Some decision)
                        (PaymentProgress.payments claim.Progress)

                let progress =
                    PaymentProgress.fromGroups
                        (Some decision)
                        (PaymentProgress.payments claim.Progress)
                    |> Result.defaultWith (fun () ->
                        invalidOp "Validated decision retains a valid payment progression.")

                return { claim with Progress = progress }
            }
            |> businessChange CaseActKind.Decide claim
        | Applicable.Withdrawing claim ->
            businessChange
                CaseActKind.WithdrawDecision
                claim
                (Ok
                    { claim with
                        Progress = PaymentProgress.Undecided
                    })
        | Applicable.Paying(claim, decision, payment) ->
            result {
                if
                    Money.currency (Payment.paid payment)
                    <> Money.currency (Decision.payable decision)
                then
                    return!
                        InputValidation.invalid
                            (InputTarget.Field BusinessField.PaidCurrency)
                            Violation.PaymentCurrencyMismatch
                elif
                    PaymentProgress.payments claim.Progress
                    |> List.exists (fun entry -> PaymentEntry.id entry = operationId)
                then
                    return! Error(Refusal.Case CaseRefusal.PaymentAlreadyRecorded)
                else
                    let entries =
                        PaymentProgress.payments claim.Progress
                        @ [ PaymentEntry.recorded operationId payment ]

                    do!
                        StateValidation.crossProgress
                            (Payment.values payment |> List.map fst |> Set.ofList)
                            (Registration.incidentNotificationDate claim.Facts)
                            (Some decision)
                            entries

                    let progress =
                        PaymentProgress.fromGroups (Some decision) entries
                        |> Result.defaultWith (fun () ->
                            invalidOp "Validated payment entries form a valid progression.")

                    return { claim with Progress = progress }
            }
            |> businessChange CaseActKind.RecordPayment claim
        | Applicable.Removing(claim, decision, id) ->
            let entries = PaymentProgress.payments claim.Progress

            if entries |> List.exists (fun entry -> PaymentEntry.id entry = id) |> not then
                businessChange
                    CaseActKind.RemovePayment
                    claim
                    (Error(Refusal.Case CaseRefusal.PaymentNotFound))
            else
                let remaining = entries |> List.filter (fun entry -> PaymentEntry.id entry <> id)

                let progress =
                    PaymentProgress.fromGroups (Some decision) remaining
                    |> Result.defaultWith (fun () ->
                        invalidOp "Removing a payment preserves the payable bound.")

                businessChange
                    CaseActKind.RemovePayment
                    claim
                    (Ok { claim with Progress = progress })
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

    let private ordered =
        function
        | Error(Refusal.InvalidInput issues) ->
            InputIssues.toList issues
            |> List.sortBy (fun issue -> InputTarget.order issue.Target)
            |> InputIssues.ofList
            |> Option.get
            |> Refusal.InvalidInput
            |> Error
        | result -> result

    let private admitBusiness today before changed =
        match before, changed with
        | Some before, Ok(CaseState.Active after) -> StateValidation.admit today (Some before) after
        | _ -> Ok()

    let apply today operationId evidence =
        let before, changed = change today operationId evidence

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

        Admission.map2 (fun next () -> next) changed admission |> ordered
