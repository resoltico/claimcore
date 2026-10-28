namespace ClaimCore.Domain

open ClaimCore.Domain.ResultFlow

/// Eligibility carries the accepted sub-state required to perform the selected change.
[<RequireQualifiedAccess; NoComparison>]
type internal Applicable<'r, 'd, 'p, 's> =
    private
    | Amending of Claim * 'r
    | Correcting of Claim * Correction<'r, 'd, 'p>
    | Deciding of Claim * 'd
    | Withdrawing of Claim
    | Paying of Claim * Decision * 'p
    | Unpaying of Claim * Decision
    | Closing of Claim
    | Reopening of Claim
    | Voiding of Claim * 's
    | Reinstating of Claim * 's
    | Erasing

module internal Transitions =
    let private amend facts claim =
        match claim.Progress with
        | PaymentProgress.Undecided -> Ok(Applicable.Amending(claim, facts))
        | PaymentProgress.Decided _
        | PaymentProgress.Paid _ -> Error CaseRefusal.AmendmentRequiresUndecided

    let private correct correction claim =
        match claim.Status, claim.Progress with
        | CaseStatus.Closed, _ -> Ok(Applicable.Correcting(claim, correction))
        | CaseStatus.Opened, PaymentProgress.Undecided ->
            Error CaseRefusal.CorrectionRequiresExistingValue
        | CaseStatus.Opened, (PaymentProgress.Decided _ | PaymentProgress.Paid _) ->
            Ok(Applicable.Correcting(claim, correction))

    let private decide decision claim =
        match claim.Progress with
        | PaymentProgress.Paid _ -> Error CaseRefusal.DecisionAlreadyPaid
        | PaymentProgress.Undecided
        | PaymentProgress.Decided _ -> Ok(Applicable.Deciding(claim, decision))

    let private withdraw claim =
        match claim.Progress with
        | PaymentProgress.Undecided -> Error CaseRefusal.DecisionRequired
        | PaymentProgress.Paid _ -> Error CaseRefusal.DecisionAlreadyPaid
        | PaymentProgress.Decided _ -> Ok(Applicable.Withdrawing claim)

    let private pay payment claim =
        match claim.Progress with
        | PaymentProgress.Undecided -> Error CaseRefusal.DecisionRequired
        | PaymentProgress.Paid _ -> Error CaseRefusal.PaymentAlreadyRecorded
        | PaymentProgress.Decided decision when Money.value decision.PayableAmount = 0M ->
            Error CaseRefusal.ZeroDecisionCannotBePaid
        | PaymentProgress.Decided decision -> Ok(Applicable.Paying(claim, decision, payment))

    let private unpay claim =
        match claim.Progress with
        | PaymentProgress.Paid(decision, _) -> Ok(Applicable.Unpaying(claim, decision))
        | PaymentProgress.Undecided
        | PaymentProgress.Decided _ -> Error CaseRefusal.PaymentNotRecorded

    let private whenOpened decide claim =
        match claim.Status with
        | CaseStatus.Closed -> Error CaseRefusal.CaseClosed
        | CaseStatus.Opened -> decide claim

    let private close claim =
        match claim.Status with
        | CaseStatus.Closed -> Error CaseRefusal.AlreadyClosed
        | CaseStatus.Opened -> Ok(Applicable.Closing claim)

    let private reopen claim =
        match claim.Status with
        | CaseStatus.Closed -> Ok(Applicable.Reopening claim)
        | CaseStatus.Opened -> Error CaseRefusal.AlreadyOpened

    let private activeBusiness claim =
        function
        | CaseAct.Open _ -> Error CaseRefusal.CaseAlreadyExists
        | CaseAct.AmendRegistration facts -> whenOpened (amend facts) claim
        | CaseAct.Correct correction -> correct correction claim
        | CaseAct.Decide decision -> whenOpened (decide decision) claim
        | CaseAct.Bare BareAct.WithdrawDecision -> whenOpened withdraw claim
        | CaseAct.RecordPayment payment -> whenOpened (pay payment) claim
        | CaseAct.Bare BareAct.ClearPayment -> whenOpened unpay claim
        | CaseAct.Bare BareAct.Close -> close claim
        | CaseAct.Bare BareAct.Reopen -> reopen claim
        | _ -> invalidOp "A stewardship act cannot enter business rules."

    let private activeStewardship claim =
        function
        | CaseAct.Void reason -> Ok(Applicable.Voiding(claim, reason))
        | CaseAct.Reinstate _ -> Error CaseRefusal.NotVoided
        | CaseAct.Bare BareAct.Erase -> Ok Applicable.Erasing
        | _ -> invalidOp "A business act cannot enter stewardship rules."

    let private active claim act =
        match (CaseActKind.describe (CaseAct.kind act)).Family with
        | ActFamily.Business -> activeBusiness claim act
        | ActFamily.Stewardship -> activeStewardship claim act

    let private voidedBusiness =
        function
        | CaseAct.Open _ -> Error CaseRefusal.CaseAlreadyExists
        | _ -> Error CaseRefusal.CaseVoided

    let private voidedStewardship claim =
        function
        | CaseAct.Void _ -> Error CaseRefusal.AlreadyVoided
        | CaseAct.Reinstate reason -> Ok(Applicable.Reinstating(claim, reason))
        | CaseAct.Bare BareAct.Erase -> Ok Applicable.Erasing
        | _ -> invalidOp "A business act cannot enter stewardship rules."

    let private voided claim act =
        match (CaseActKind.describe (CaseAct.kind act)).Family with
        | ActFamily.Business -> voidedBusiness act
        | ActFamily.Stewardship -> voidedStewardship claim act

    let applicable state act =
        match state with
        | CaseState.Erased -> Error CaseRefusal.CaseErased
        | CaseState.Voided claim -> voided claim act
        | CaseState.Active claim -> active claim act

    let private businessChange before after =
        Some before, after |> Result.map CaseState.Active

    /// The proof supplies the decision for payment changes; no state shape is recovered by a partial operation.
    let private change evidence =
        match evidence with
        | Applicable.Amending(claim, facts) ->
            businessChange claim (Ok { claim with Facts = facts })
        | Applicable.Correcting(claim, correction) ->
            CaseCorrections.apply claim.Facts claim.Progress correction
            |> Result.map (fun (facts, progress) ->
                { claim with
                    Facts = facts
                    Progress = progress
                })
            |> businessChange claim
        | Applicable.Deciding(claim, decision) ->
            businessChange
                claim
                (Ok
                    { claim with
                        Progress = PaymentProgress.Decided decision
                    })
        | Applicable.Withdrawing claim ->
            businessChange
                claim
                (Ok
                    { claim with
                        Progress = PaymentProgress.Undecided
                    })
        | Applicable.Paying(claim, decision, payment) ->
            businessChange
                claim
                (Ok
                    { claim with
                        Progress = PaymentProgress.Paid(decision, payment.PaymentDate)
                    })
        | Applicable.Unpaying(claim, decision) ->
            businessChange
                claim
                (Ok
                    { claim with
                        Progress = PaymentProgress.Decided decision
                    })
        | Applicable.Closing claim ->
            businessChange
                claim
                (Ok
                    { claim with
                        Status = CaseStatus.Closed
                    })
        | Applicable.Reopening claim ->
            businessChange
                claim
                (Ok
                    { claim with
                        Status = CaseStatus.Opened
                    })
        | Applicable.Voiding(claim, reason) ->
            Some claim,
            Reason.admit reason
            |> Result.map (fun _ -> CaseState.Voided claim)
            |> Result.mapError (fun violation ->
                Refusal.invalidInput (InputTarget.Reason, violation))
        | Applicable.Reinstating(claim, reason) ->
            Some claim,
            Reason.admit reason
            |> Result.map (fun _ -> CaseState.Active claim)
            |> Result.mapError (fun violation ->
                Refusal.invalidInput (InputTarget.Reason, violation))
        | Applicable.Erasing ->
            // Erasure consumes no business payload and performs no new scalar admission.
            None, Ok CaseState.Erased

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

    let apply today evidence =
        let before, changed = change evidence

        let admission =
            match evidence with
            | Applicable.Correcting(before, correction) ->
                CaseCorrections.proposedGroups before.Facts before.Progress correction
                |> Result.mapError Refusal.Case
                |> Result.bind (StateValidation.admitGroups today (Some before))
            | Applicable.Voiding _
            | Applicable.Reinstating _
            | Applicable.Erasing -> Ok()
            | Applicable.Amending _
            | Applicable.Deciding _
            | Applicable.Withdrawing _
            | Applicable.Paying _
            | Applicable.Unpaying _
            | Applicable.Closing _
            | Applicable.Reopening _ -> admitBusiness today before changed

        Admission.map2 (fun next () -> next) changed admission |> ordered
