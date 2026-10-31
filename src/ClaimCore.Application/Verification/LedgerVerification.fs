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
            Accepted: Map<OperationId, ActorId>
            References: Map<string, CaseId>
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
                (fun reference -> Map.tryFind (CaseReference.key reference) state.References)
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

            let subject =
                match entry.Subject with
                | EntryTarget.NonAcceptance operation -> RecordedTarget.NonAcceptance operation
                | EntryTarget.Authority(id, act) ->
                    RecordedTarget.Authority { TargetActorId = id; Act = act }
                | EntryTarget.Case(id, revision, act, date) ->
                    RecordedTarget.Case
                        {
                            CaseId = id
                            Revision = revision
                            Act = act
                            BusinessDate = date
                        }

            do!
                require
                    at
                    VerificationFinding.PayloadFormat
                    (metadata.InstallationId = installationId
                     && metadata.Sequence = entry.Sequence
                     && metadata.OperationId = entry.OperationId
                     && metadata.ActorId = entry.ActorId
                     && metadata.RecordedAt = entry.RecordedAt
                     && metadata.Subject = subject
                     && metadata.CreationReference = entry.CreationReference
                     && metadata.PayloadCommitment = entry.PayloadCommitment)

            do! link state sequence metadata.PreviousHash

            let! hash =
                capture at VerificationFinding.PayloadFormat (fun () -> LedgerEntry.hash metadata)

            do! require at VerificationFinding.EntryHash (hash = entry.EntryHash)
            let! payload = VerificationPayload.read installationId entry

            do!
                match payload with
                | VerifiedPayload.NonAcceptance operation ->
                    require
                        at
                        VerificationFinding.PayloadFormat
                        (not (Map.containsKey (entry.ActorId, operation) state.Fences)
                         && Map.tryFind operation state.Accepted <> Some entry.ActorId)
                | _ ->
                    require
                        at
                        VerificationFinding.PayloadFormat
                        (not (Map.containsKey (entry.ActorId, entry.OperationId) state.Fences)
                         && not (Map.containsKey entry.OperationId state.Accepted))

            let! references =
                match entry.Subject, entry.CreationReference with
                | EntryTarget.Case(id, _, CaseActKind.Open, _), Some reference ->
                    let key = CaseReference.identity reference

                    require
                        at
                        VerificationFinding.HistoryStart
                        (not (Map.containsKey key state.References))
                    |> Result.map (fun () -> Map.add key id state.References)
                | EntryTarget.Case(_, _, CaseActKind.Open, _), None ->
                    Error(divergence at VerificationFinding.HistoryStart)
                | _, None -> Ok state.References
                | _, Some _ -> Error(divergence at VerificationFinding.PayloadFormat)

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
                        | _ -> Map.add entry.OperationId entry.ActorId state.Accepted
                    References = references
                    Fences =
                        match payload with
                        | VerifiedPayload.NonAcceptance operation ->
                            Map.add (entry.ActorId, operation) entry.RecordedAt state.Fences
                        | _ -> state.Fences
                    ExpectedSeen = state.ExpectedSeen || seen
                }
        }

    let initial (expected: LedgerPosition option) =
        let head = LedgerPosition.genesis

        {
            Head = head
            Cases = Map.empty
            Actors = Map.empty
            Fences = Map.empty
            Accepted = Map.empty
            References = Map.empty
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
