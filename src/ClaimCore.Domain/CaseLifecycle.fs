namespace ClaimCore.Domain

open System
open CaseLifecycleValidation

type private LifecycleReference =
    | LiveReference of string
    | SuppressedReference of byte array

type LifecycleState =
    private
        {
            CaseId: Guid
            Reference: LifecycleReference
            Revision: int64
            Disposition: CaseDisposition
            Privacy: PrivacyPhase
            Holds: Map<Guid, LifecycleHold>
        }

type DispositionTransition =
    {
        State: LifecycleState
        Snapshot: CaseView
    }

/// Pure decisions. Persistence, actor grants, witnessed ordering and completeness of copy inventory
/// remain Application/storage responsibilities; none of these functions claims external proof.
module CaseLifecycle =
    let initial caseId (snapshot: CaseView) =
        if
            caseId = Guid.Empty
            || snapshot.Version < 1L
            || String.IsNullOrEmpty snapshot.Fields.CaseReference
        then
            Error LifecycleRefusal.InvalidIdentity
        else
            Ok
                {
                    CaseId = caseId
                    Reference = LiveReference snapshot.Fields.CaseReference
                    Revision = snapshot.Version
                    Disposition = CaseDisposition.Active
                    Privacy = PrivacyPhase.Active
                    Holds = Map.empty
                }

    /// Rehydrate the current SQL projection without manufacturing historical transitions.
    /// The accepted-event auditor independently replays the events before trusting this view.
    let internal restoreProjection caseId snapshot disposition privacy holds =
        match initial caseId snapshot with
        | Error refusal -> Error refusal
        | Ok state ->
            if not (validRestoredHolds holds) then
                Error LifecycleRefusal.InvalidIdentity
            elif privacy = PrivacyPhase.ErasureFinal && not holds.IsEmpty then
                Error LifecycleRefusal.HoldActive
            else
                Ok
                    { state with
                        Disposition = disposition
                        Privacy = privacy
                        Holds = holds |> List.map (fun hold -> hold.Id, hold) |> Map.ofList
                    }

    /// A purged case carries only a protected commitment, never a fabricated business row.
    let internal restoreTombstone caseId revision disposition privacy (commitment: byte array) =
        if
            caseId = Guid.Empty
            || revision < 1L
            || isNull (box commitment)
            || commitment.Length <> 32
        then
            Error LifecycleRefusal.InvalidIdentity
        elif privacy = PrivacyPhase.Active || privacy = PrivacyPhase.ErasureRequested then
            Error LifecycleRefusal.WrongPrivacyPhase
        else
            Ok
                {
                    CaseId = caseId
                    Reference = SuppressedReference(Array.copy commitment)
                    Revision = revision
                    Disposition = disposition
                    Privacy = privacy
                    Holds = Map.empty
                }

    let disposition state = state.Disposition
    let privacy state = state.Privacy
    let holds state = state.Holds |> Map.values |> Seq.toList
    let revision state = state.Revision

    let internal ownerErasureContext state =
        state.CaseId,
        state.Revision,
        state.Privacy,
        state.Holds.IsEmpty,
        (match state.Reference with
         | SuppressedReference _ -> true
         | LiveReference _ -> false)

    let internal ownerErasureTransition state privacy = { state with Privacy = privacy }

    let permits access state =
        match access with
        | LifecycleAccess.CustodianAudit ->
            state.Privacy = PrivacyPhase.Active
            || state.Privacy = PrivacyPhase.ErasureRequested
            || state.Privacy = PrivacyPhase.ErasurePending
        | _ ->
            state.Disposition = CaseDisposition.Active
            && state.Privacy = PrivacyPhase.Active

    let private checkBinding state (snapshot: CaseView) decision expectedAction =
        let referenceMismatch =
            match state.Reference with
            | LiveReference reference -> snapshot.Fields.CaseReference <> reference
            | SuppressedReference _ -> true

        CaseLifecycleValidation.checkBinding
            state.CaseId
            state.Revision
            referenceMismatch
            snapshot.Version
            decision
            expectedAction

    let private acceptedDisposition state snapshot disposition =
        let nextRevision = state.Revision + 1L

        {
            State =
                { state with
                    Revision = nextRevision
                    Disposition = disposition
                }
            Snapshot = { snapshot with Version = nextRevision }
        }

    let voidDataEntryError state snapshot paymentEvidence decision =
        match checkBinding state snapshot decision LifecycleAction.VoidDataEntryError with
        | Error refusal -> Error refusal
        | Ok() when state.Privacy <> PrivacyPhase.Active -> Error LifecycleRefusal.ErasureHasBegun
        | Ok() when state.Disposition <> CaseDisposition.Active ->
            Error LifecycleRefusal.WrongDisposition
        | Ok() ->
            let requiresDual =
                LifecycleEvidenceAuthority.requiresDualPayment paymentEvidence
                || snapshot.Fields.PaymentDate.IsSome

            match
                if requiresDual then
                    LifecycleEvidenceAuthority.checkApprovals decision
                else
                    Ok()
            with
            | Error refusal -> Error refusal
            | Ok() -> Ok(acceptedDisposition state snapshot CaseDisposition.VoidedDataEntryError)

    let reinstateVoided state snapshot decision =
        match checkBinding state snapshot decision LifecycleAction.ReinstateVoided with
        | Error refusal -> Error refusal
        | Ok() when state.Privacy <> PrivacyPhase.Active -> Error LifecycleRefusal.ErasureHasBegun
        | Ok() when state.Disposition <> CaseDisposition.VoidedDataEntryError ->
            Error LifecycleRefusal.WrongDisposition
        | Ok() ->
            match LifecycleEvidenceAuthority.checkApprovals decision with
            | Error refusal -> Error refusal
            | Ok() -> Ok(acceptedDisposition state snapshot CaseDisposition.Active)

    let recordHold state hold =
        match checkHold hold with
        | Error refusal -> Error refusal
        | Ok() when state.Privacy = PrivacyPhase.ErasureFinal ->
            Error LifecycleRefusal.WrongPrivacyPhase
        | Ok() when state.Holds.ContainsKey hold.Id -> Error LifecycleRefusal.DuplicateHold
        | Ok() when state.Holds.Count >= 256 -> Error LifecycleRefusal.HoldCapacityExceeded
        | Ok() ->
            Ok
                { state with
                    Holds = state.Holds.Add(hold.Id, hold)
                }

    let releaseHold state holdId actorId at reason =
        if actorId = Guid.Empty || holdId = Guid.Empty then
            Error LifecycleRefusal.InvalidIdentity
        elif not (validInstant at) then
            Error LifecycleRefusal.InvalidTime
        elif not (validReason reason) then
            Error LifecycleRefusal.InvalidReason
        elif not (state.Holds.ContainsKey holdId) then
            Error LifecycleRefusal.HoldNotFound
        else
            Ok
                { state with
                    Holds = state.Holds.Remove holdId
                }

    let requestErasure state actorId at reason =
        if actorId = Guid.Empty then
            Error LifecycleRefusal.InvalidIdentity
        elif not (validInstant at) then
            Error LifecycleRefusal.InvalidTime
        elif not (validReason reason) then
            Error LifecycleRefusal.InvalidReason
        elif state.Privacy <> PrivacyPhase.Active then
            Error LifecycleRefusal.WrongPrivacyPhase
        else
            Ok
                { state with
                    Privacy = PrivacyPhase.ErasureRequested
                }

    let markErasurePending state witnessedFence actorId at reason =
        if actorId = Guid.Empty then
            Error LifecycleRefusal.InvalidIdentity
        elif not (validInstant at) then
            Error LifecycleRefusal.InvalidTime
        elif not (validReason reason) then
            Error LifecycleRefusal.InvalidReason
        elif state.Privacy <> PrivacyPhase.ErasureRequested then
            Error LifecycleRefusal.WrongPrivacyPhase
        elif not (LifecycleEvidenceAuthority.matchesFence state.CaseId witnessedFence) then
            Error LifecycleRefusal.ErasureEvidenceIncomplete
        else
            Ok
                { state with
                    Privacy = PrivacyPhase.ErasurePending
                }

    /// Owner-only live purge leaves the case in ERASURE_PENDING until every managed copy is verified absent.
    let authorizeLivePurge state decision =
        if state.Privacy <> PrivacyPhase.ErasurePending then
            Error LifecycleRefusal.WrongPrivacyPhase
        elif not state.Holds.IsEmpty then
            Error LifecycleRefusal.HoldActive
        elif
            match state.Reference with
            | LiveReference _ -> false
            | SuppressedReference _ -> true
        then
            Error LifecycleRefusal.WrongPrivacyPhase
        else
            checkErasureDecision
                state.CaseId
                state.Revision
                decision
                LifecycleAction.PurgeLivePayload

    let authorizeOwnerLivePurge state decision =
        if state.Privacy <> PrivacyPhase.ErasurePending then
            Error LifecycleRefusal.WrongPrivacyPhase
        elif not state.Holds.IsEmpty then
            Error LifecycleRefusal.HoldActive
        elif
            match state.Reference with
            | LiveReference _ -> false
            | SuppressedReference _ -> true
        then
            Error LifecycleRefusal.WrongPrivacyPhase
        else
            checkOwnerPurgeDecision state.CaseId state.Revision decision
