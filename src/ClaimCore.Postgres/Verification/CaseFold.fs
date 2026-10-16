namespace ClaimCore.Postgres

open System
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres.VerificationChecks

type internal CaseHistory =
    {
        Revision: int64
        Snapshot: CaseSnapshot option
        Reference: string option
        Disposition: Disposition
        Erased: bool
        MissingPayloadAt: int64 option
        PresentPayloadAt: int64 option
        ErasedAt: int64 option
    }

module internal CaseFold =
    let private nextDisposition sequence previous act =
        let disposition =
            previous |> Option.map _.Disposition |> Option.defaultValue Disposition.Active

        let erased = previous |> Option.exists _.Erased

        match act, disposition with
        | _ when erased -> Error(divergence (Some sequence) VerificationFinding.DispositionSequence)
        | CaseActKind.Void, Disposition.Active -> Ok Disposition.Voided
        | CaseActKind.Reinstate, Disposition.Voided -> Ok Disposition.Active
        | CaseActKind.Void, Disposition.Voided
        | CaseActKind.Reinstate, Disposition.Active ->
            Error(divergence (Some sequence) VerificationFinding.DispositionSequence)
        | CaseActKind.Erase, _ -> Ok disposition
        | _, Disposition.Voided ->
            Error(divergence (Some sequence) VerificationFinding.DispositionSequence)
        | _ -> Ok Disposition.Active

    let private snapshot sequence disposition reference payload =
        match payload with
        | VerifiedPayload.Case(_, Some snapshot) ->
            let matches =
                match snapshot.View with
                | CaseView.Live(_, value) ->
                    value = disposition && (reference |> Option.forall ((=) snapshot.Reference))
                | _ -> false

            require (Some sequence) VerificationFinding.DispositionSequence matches
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
                    ((previous.IsNone) = (act = CaseActKind.Open))

            let! disposition = nextDisposition entry.Sequence previous act
            let reference = previous |> Option.bind _.Reference
            let! snapshot, reference = snapshot entry.Sequence disposition reference payload
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
                        Disposition = disposition
                        Erased = act = CaseActKind.Erase
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
            if history.Erased && history.PresentPayloadAt.IsSome then
                Some(divergence history.ErasedAt VerificationFinding.ErasureIncomplete)
            elif not history.Erased && history.MissingPayloadAt.IsSome then
                Some(divergence history.MissingPayloadAt VerificationFinding.PayloadWithoutErasure)
            else
                None)
        |> Seq.sortBy _.Sequence
        |> Seq.tryHead
        |> function
            | Some finding -> Error finding
            | None -> Ok()

    let projection installationId histories (cases: StoredCase list) =
        let matches (record: StoredCase) =
            match Map.tryFind record.CaseId histories with
            | None -> false
            | Some history ->
                let identity =
                    ClaimCore.RecordFormat.CaseReferenceId.encode
                        installationId
                        record.Snapshot.Reference
                        =
                        record.CaseId

                let state =
                    match record.Snapshot.View with
                    | CaseView.Erased -> history.Erased && history.MissingPayloadAt.IsSome
                    | CaseView.Live(_, disposition) ->
                        not history.Erased
                        && history.MissingPayloadAt.IsNone
                        && history.Snapshot = Some record.Snapshot
                        && disposition = history.Disposition

                identity && record.Snapshot.Revision = history.Revision && state

        require
            None
            VerificationFinding.CaseProjection
            (cases.Length = Map.count histories
             && (cases |> List.distinctBy _.CaseId |> List.length) = cases.Length
             && List.forall matches cases)
