namespace ClaimCore.Application


open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application.VerificationChecks

type CaseHistory =
    {
        Revision: Revision
        ChangedAt: DateTimeOffset
        Snapshot: CaseSnapshot option
        Reference: CaseReference option
        State: CaseState<unit>
        MissingPayloadAt: Sequence option
        PresentPayloadAt: Sequence option
        ErasedAt: Sequence option
    }

module internal CaseFold =
    let private nextState sequence previous act =
        let state =
            previous |> Option.map _.State |> Option.defaultValue (CaseState.Active())

        let refuse () =
            Error(divergence (Some sequence) VerificationFinding.StateSequence)

        match act, state with
        | _, CaseState.Erased -> refuse ()
        | CaseActKind.Void, CaseState.Active() -> Ok(CaseState.Voided())
        | CaseActKind.Reinstate, CaseState.Voided() -> Ok(CaseState.Active())
        | CaseActKind.Void, CaseState.Voided()
        | CaseActKind.Reinstate, CaseState.Active() -> refuse ()
        | CaseActKind.Erase, _ -> Ok CaseState.Erased
        | _, CaseState.Voided() -> refuse ()
        | _, CaseState.Active() -> Ok(CaseState.Active())

    let private snapshot sequence state reference payload =
        match payload with
        | VerifiedPayload.Case(_, Some snapshot) ->
            let matches =
                CaseState.map ignore snapshot.State = state
                && (reference |> Option.forall ((=) snapshot.Reference))

            require (Some sequence) VerificationFinding.StateSequence matches
            |> Result.map (fun () -> Some snapshot, Some snapshot.Reference)
        | VerifiedPayload.Case(_, None)
        | VerifiedPayload.Erased -> Ok(None, reference)
        | _ -> Error(divergence (Some sequence) VerificationFinding.PayloadFormat)

    let apply histories (entry: StoredEntry) payload =
        match entry.Subject with
        | EntryTarget.Authority _ ->
            Error(divergence (Some entry.Sequence) VerificationFinding.PayloadFormat)
        | EntryTarget.Case(id, revision, act, _) ->
            result {
                let previous = Map.tryFind id histories

                let expected =
                    previous |> Option.map _.Revision |> Option.defaultValue Revision.zero

                do!
                    require
                        (Some entry.Sequence)
                        VerificationFinding.RevisionSequence
                        (Revision.value expected < Revision.Maximum
                         && revision = Revision.successor expected)

                do!
                    require
                        (Some entry.Sequence)
                        VerificationFinding.HistoryStart
                        ((previous.IsNone) = (act = CaseActKind.Open))

                do!
                    match payload with
                    | VerifiedPayload.Case(request, snapshot) ->
                        let expected =
                            ClaimCore.RecordFormat.HistoricalCaseEffects.apply
                                request
                                (previous |> Option.bind _.Snapshot)

                        let agrees =
                            match expected, snapshot with
                            | Ok expected, Some actual -> expected = actual
                            | Ok { State = CaseState.Erased }, None -> true
                            | _ -> false

                        require (Some entry.Sequence) VerificationFinding.StateSequence agrees
                    | _ -> Ok()

                let! state = nextState entry.Sequence previous act
                let reference = previous |> Option.bind _.Reference
                let! snapshot, reference = snapshot entry.Sequence state reference payload
                let first field = previous |> Option.bind field

                let missing =
                    if entry.Payload.IsNone then
                        first _.MissingPayloadAt |> Option.orElse (Some entry.Sequence)
                    else
                        first _.MissingPayloadAt

                let present =
                    if entry.Payload.IsSome then
                        first _.PresentPayloadAt |> Option.orElse (Some entry.Sequence)
                    else
                        first _.PresentPayloadAt

                return
                    Map.add
                        id
                        {
                            Revision = revision
                            ChangedAt = entry.RecordedAt
                            Snapshot = snapshot
                            Reference = reference
                            State = state
                            MissingPayloadAt = missing
                            PresentPayloadAt = present
                            ErasedAt =
                                if act = CaseActKind.Erase then
                                    Some entry.Sequence
                                else
                                    None
                        }
                        histories
            }

    let erasure histories =
        histories
        |> Map.values
        |> Seq.choose (fun history ->
            if history.State = CaseState.Erased && history.PresentPayloadAt.IsSome then
                Some(divergence history.ErasedAt VerificationFinding.ErasureIncomplete)
            elif history.State <> CaseState.Erased && history.MissingPayloadAt.IsSome then
                Some(divergence history.MissingPayloadAt VerificationFinding.PayloadWithoutErasure)
            else
                None)
        |> Seq.sortBy _.Sequence
        |> Seq.tryHead
        |> function
            | Some finding -> Error finding
            | None -> Ok()

    let matchesProjection installationId histories (record: StoredCase) =
        match Map.tryFind record.CaseId histories with
        | None -> false
        | Some history ->
            let identity =
                ClaimCore.RecordFormat.CaseReferenceId.encode
                    installationId
                    (CaseReference.format record.Snapshot.Reference)
                    =
                    record.CaseId

            let state =
                match record.Snapshot.State with
                | CaseState.Erased ->
                    history.State = CaseState.Erased
                    && history.MissingPayloadAt.IsSome
                    && Array.isEmpty record.SnapshotBytes
                | CaseState.Active _
                | CaseState.Voided _ ->
                    history.State <> CaseState.Erased
                    && history.MissingPayloadAt.IsNone
                    && history.Snapshot = Some record.Snapshot
                    && (history.Snapshot
                        |> Option.exists (fun snapshot ->
                            ClaimCore.RecordFormat.CaseRecord.encodeSnapshot snapshot =
                                record.SnapshotBytes))
                    && CaseState.map ignore record.Snapshot.State = history.State

            identity && record.Snapshot.Revision = history.Revision && state
