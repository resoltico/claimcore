namespace ClaimCore.Application


open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Application.VerificationChecks

/// Deterministic ledger interpretation independent of storage, current admission and clocks.
module LedgerVerification =
    type Scan =
        {
            Head: LedgerPosition
            Cases: Map<CaseId, CaseHistory>
            Actors: Map<ActorId, ActorAuthority>
            Fences: Map<ActorId * OperationId, DateTimeOffset>
            Accepted: Set<ActorId * OperationId>
            EarliestDuplicate: Sequence option
            ExpectedSeen: bool
        }

    let private histories installationId state (entry: StoredEntry) payload =
        match entry.Subject, payload with
        | EntryTarget.NonAcceptance _, VerifiedPayload.NonAcceptance _ ->
            AuthorityFold.executor state.Actors entry false false
            |> Result.map (fun () -> state.Cases, state.Actors)
        | EntryTarget.Authority _, VerifiedPayload.Authority request ->
            AuthorityFold.apply
                installationId
                (fun reference ->
                    state.Cases
                    |> Map.toSeq
                    |> Seq.tryPick (fun (id, history) ->
                        if
                            history.Reference
                            |> Option.exists (fun retained ->
                                CaseReference.identity retained = CaseReference.key reference)
                        then
                            Some id
                        else
                            None))
                state.Actors
                entry
                request
            |> Result.map (fun actors -> state.Cases, actors)
        | EntryTarget.Case(_, _, act, _), _ ->
            result {
                let! cases = CaseFold.apply state.Cases entry payload

                let human =
                    match act with
                    | CaseActKind.Void
                    | CaseActKind.Reinstate
                    | CaseActKind.Erase -> true
                    | _ -> false

                do! AuthorityFold.executor state.Actors entry human false
                return cases, state.Actors
            }
        | _ -> Error(divergence (Some entry.Sequence) VerificationFinding.PayloadFormat)

    let link (state: Scan) sequence previous =
        require
            (Some sequence)
            VerificationFinding.Link
            (Sequence.value state.Head.Sequence < Sequence.Maximum
             && sequence = Sequence.successor state.Head.Sequence
             && previous = EntryHash.toBytes state.Head.EntryHash)

    let append
        installationId
        (expected: LedgerPosition option)
        (state: Scan)
        (entry: StoredEntry)
        (metadata: LedgerMetadata)
        =
        result {
            let sequence = entry.Sequence
            let at = Some sequence
            do! link state sequence metadata.PreviousHash

            let! hash =
                capture at VerificationFinding.PayloadFormat (fun () -> LedgerEntry.hash metadata)

            do! require at VerificationFinding.EntryHash (hash = entry.EntryHash)
            let! payload = VerificationPayload.read installationId entry

            do!
                require
                    at
                    VerificationFinding.PayloadFormat
                    (state.EarliestDuplicate <> Some sequence)

            do!
                match payload with
                | VerifiedPayload.NonAcceptance operation ->
                    require
                        at
                        VerificationFinding.PayloadFormat
                        (not (Map.containsKey (entry.ActorId, operation) state.Fences)
                         && not (Set.contains (entry.ActorId, operation) state.Accepted))
                | _ ->
                    require
                        at
                        VerificationFinding.PayloadFormat
                        (not (Map.containsKey (entry.ActorId, entry.OperationId) state.Fences))

            let! cases, actors = histories installationId state entry payload

            let seen =
                expected
                |> Option.exists (fun position ->
                    position.Sequence = sequence
                    && EntryHash.toBytes position.EntryHash = entry.EntryHash)

            return
                {
                    Head =
                        {
                            Sequence = sequence
                            EntryHash = EntryHash.ofBytes entry.EntryHash |> Option.get
                        }
                    Cases = cases
                    Actors = actors
                    Accepted =
                        match payload with
                        | VerifiedPayload.NonAcceptance _ -> state.Accepted
                        | _ -> Set.add (entry.ActorId, entry.OperationId) state.Accepted
                    Fences =
                        match payload with
                        | VerifiedPayload.NonAcceptance operation ->
                            Map.add (entry.ActorId, operation) entry.RecordedAt state.Fences
                        | _ -> state.Fences
                    EarliestDuplicate = state.EarliestDuplicate
                    ExpectedSeen = state.ExpectedSeen || seen
                }
        }

    let initial (expected: LedgerPosition option) earliestDuplicate =
        let head = LedgerPosition.genesis

        {
            Head = head
            Cases = Map.empty
            Actors = Map.empty
            Fences = Map.empty
            Accepted = Set.empty
            EarliestDuplicate = earliestDuplicate
            ExpectedSeen =
                expected
                |> Option.exists (fun position ->
                    position.Sequence = Sequence.zero && position.EntryHash = head.EntryHash)
        }

    let head (state: Scan) (actual: LedgerPosition option) =
        require None VerificationFinding.Head (actual = Some state.Head)

    let complete (expected: LedgerPosition option) (state: Scan) =
        if expected.IsSome && not state.ExpectedSeen then
            Error(
                divergence (expected |> Option.map _.Sequence) VerificationFinding.ExpectedPosition
            )
        else
            Ok(state.Head)

    let erasure (state: Scan) = CaseFold.erasure state.Cases
