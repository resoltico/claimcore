namespace ClaimCore.Postgres

open System
open System.IO
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat

type internal LifecycleAuditState =
    {
        Disposition: CaseDisposition
        Privacy: PrivacyPhase
        ActiveHolds: Map<Guid, LifecycleHold>
        Sequence: int64
        EventHash: byte array
        Revision: int64
        LastSnapshot: CaseView option
        LastBusinessSnapshot: CaseView option
        Reference: string option
        HistoricalPayment: bool
        WitnessedErasureRequest: (Guid * string) option
        DispositionCount: int64
        HoldCreationCount: int64
    }

module internal CaseLifecycleAuditReplay =
    let private reject () : 'a =
        raise (InvalidDataException("Lifecycle event replay differs."))

    let initial =
        {
            Disposition = CaseDisposition.Active
            Privacy = PrivacyPhase.Active
            ActiveHolds = Map.empty
            Sequence = 0L
            EventHash = Array.zeroCreate 32
            Revision = 0L
            LastSnapshot = None
            LastBusinessSnapshot = None
            Reference = None
            HistoricalPayment = false
            WitnessedErasureRequest = None
            DispositionCount = 0L
            HoldCreationCount = 0L
        }

    let private decodeBusiness revision bytes =
        match CaseRecord.decodeSnapshot bytes with
        | Ok value when CaseRecord.encodeSnapshot value = bytes && value.Version = revision ->
            match Claim.restore value with
            | Ok claim -> Claim.view claim
            | Error _ -> reject ()
        | _ -> reject ()

    let appendBusiness (state: LifecycleAuditState) (revision, bytes) =
        let value = decodeBusiness revision bytes

        if
            revision <> state.Revision + 1L
            || (state.Reference.IsSome && state.Reference <> Some value.Fields.CaseReference)
        then
            reject ()

        { state with
            Revision = revision
            LastSnapshot = Some value
            LastBusinessSnapshot = Some value
            Reference = Some value.Fields.CaseReference
            HistoricalPayment = state.HistoricalPayment || value.Fields.PaymentDate.IsSome
        }

    let private domainDecision
        caseId
        (state: LifecycleAuditState)
        (event: LifecycleAuditDecodedEvent)
        (draftHash: byte array)
        (approvals: LifecycleApprovalEvidence list)
        =
        let before = state.LastSnapshot |> Option.defaultWith reject
        let active = state.ActiveHolds |> Map.toList |> List.map snd

        let current =
            CaseLifecycleDecisions.restore caseId before state.Disposition state.Privacy active
            |> Result.defaultWith (fun _ -> reject ())

        CaseLifecycleDecisions.decide
            caseId
            before
            current
            event.Change
            event.ActorId
            (Convert.ToHexStringLower draftHash)
            state.HistoricalPayment
            state.WitnessedErasureRequest
            approvals
            event.Instant
        |> Result.defaultWith (fun _ -> reject ())

    let private advanceSnapshot
        (state: LifecycleAuditState)
        (event: LifecycleAuditDecodedEvent)
        (decision: LifecycleDecisionResult)
        =
        match decision.Snapshot, event.Snapshot with
        | None, None -> state.Revision, state.LastSnapshot, state.DispositionCount
        | Some value, Some bytes ->
            if value.Version <> state.Revision + 1L || CaseRecord.encodeSnapshot value <> bytes then
                reject ()

            value.Version, Some value, state.DispositionCount + 1L
        | _ -> reject ()

    let step
        caseId
        (state: LifecycleAuditState)
        (row: LifecycleAuditEventRow)
        (event: LifecycleAuditDecodedEvent)
        (approvals: LifecycleApprovalEvidence list)
        =
        if
            event.Change.ExpectedRevision <> state.Revision
            || event.Change.ExpectedLifecycleSequence <> state.Sequence
            || state.Reference <> Some event.Change.CaseReference
        then
            reject ()

        let decision = domainDecision caseId state event row.DraftHash approvals
        let revision, snapshot, dispositionCount = advanceSnapshot state event decision

        if
            CaseLifecycle.revision decision.State <> event.BusinessRevision
            || CaseLifecycle.disposition decision.State <> event.Disposition
            || CaseLifecycle.privacy decision.State <> event.Privacy
        then
            reject ()

        let active =
            CaseLifecycle.holds decision.State
            |> List.map (fun hold -> hold.Id, hold)
            |> Map.ofList

        if active.Count > 256 then
            reject ()

        { state with
            Disposition = event.Disposition
            Privacy = event.Privacy
            ActiveHolds = active
            Sequence = state.Sequence + 1L
            EventHash = row.EventHash
            Revision = revision
            LastSnapshot = snapshot
            DispositionCount = dispositionCount
            WitnessedErasureRequest =
                match event.Change.Action with
                | LifecycleMutation.RequestErasure _ ->
                    Some(row.EventId, Convert.ToHexStringLower row.CandidateHash)
                | _ -> state.WitnessedErasureRequest
            HoldCreationCount =
                state.HoldCreationCount
                + (match event.Change.Action with
                   | LifecycleMutation.RecordHold _ -> 1L
                   | _ -> 0L)
        }

    let finish
        (state: LifecycleAuditState)
        (currentClaim: Claim)
        disposition
        privacy
        sequence
        eventHash
        holdCount
        activeHoldCount
        =
        let final = Claim.view currentClaim

        if
            state.LastSnapshot <> Some final
            || state.LastBusinessSnapshot.IsNone
            || state.Reference <> Some final.Fields.CaseReference
            || state.Revision <> final.Version
            || disposition <> state.Disposition
            || privacy <> state.Privacy
            || sequence <> state.Sequence
            || eventHash <> state.EventHash
            || holdCount <> state.HoldCreationCount
            || activeHoldCount <> int64 state.ActiveHolds.Count
        then
            reject ()

        state.LastBusinessSnapshot |> Option.defaultWith reject
