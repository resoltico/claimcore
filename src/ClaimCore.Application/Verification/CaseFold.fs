namespace ClaimCore.Application

open ClaimCore.Contract

open System
open ClaimCore.Domain
open ClaimCore.Application.VerificationChecks

type internal CaseHistory =
    {
        Revision: int64
        Snapshot: CaseSnapshot option
        Reference: CaseReference option
        State: CaseState<unit>
        MissingPayloadAt: int64 option
        PresentPayloadAt: int64 option
        ErasedAt: int64 option
    }

module internal CaseFold =
    let private nextState sequence previous act =
        let state =
            previous |> Option.map _.State |> Option.defaultValue (CaseState.Active())

        let refuse () =
            Error(divergence (Some sequence) VerificationFinding.StateSequence)

        match act, state with
        | _, CaseState.Erased -> refuse ()
        | CaseActKind.Stewardship StewardshipActKind.Void, CaseState.Active() ->
            Ok(CaseState.Voided())
        | CaseActKind.Stewardship StewardshipActKind.Reinstate, CaseState.Voided() ->
            Ok(CaseState.Active())
        | CaseActKind.Stewardship StewardshipActKind.Void, CaseState.Voided()
        | CaseActKind.Stewardship StewardshipActKind.Reinstate, CaseState.Active() -> refuse ()
        | CaseActKind.Stewardship StewardshipActKind.Erase, _ -> Ok CaseState.Erased
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

    let apply histories (entry: StoredEntry) id revision act payload =
        result {
            let previous = Map.tryFind id histories
            let expected = previous |> Option.map _.Revision |> Option.defaultValue 0L

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.RevisionSequence
                    (expected < Revision.Maximum && revision = expected + 1L)

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.HistoryStart
                    ((previous.IsNone) = (act = (CaseActKind.Business BusinessActKind.Open)))

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
                        Snapshot = snapshot
                        Reference = reference
                        State = state
                        MissingPayloadAt = missing
                        PresentPayloadAt = present
                        ErasedAt =
                            if act = (CaseActKind.Stewardship StewardshipActKind.Erase) then
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
                    history.State = CaseState.Erased && history.MissingPayloadAt.IsSome
                | CaseState.Active _
                | CaseState.Voided _ ->
                    history.State <> CaseState.Erased
                    && history.MissingPayloadAt.IsNone
                    && history.Snapshot = Some record.Snapshot
                    && CaseState.map ignore record.Snapshot.State = history.State

            identity && record.Snapshot.Revision = history.Revision && state

    let projection installationId histories (cases: StoredCase list) =
        require
            None
            VerificationFinding.CaseProjection
            (cases.Length = Map.count histories
             && (cases |> List.distinctBy _.CaseId |> List.length) = cases.Length
             && List.forall (matchesProjection installationId histories) cases)
