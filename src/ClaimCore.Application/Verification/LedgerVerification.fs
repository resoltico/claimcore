namespace ClaimCore.Application

open ClaimCore.Contract

open System
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Application.VerificationChecks

/// Deterministic ledger interpretation independent of storage, current admission and clocks.
module internal LedgerVerification =
    type Scan =
        {
            Head: LedgerHead
            Cases: Map<Guid, CaseHistory>
            Actors: Map<Guid, ActorAuthority>
            EarliestDuplicate: int64 option
            ExpectedSeen: bool
        }

    let private histories installationId state (entry: StoredEntry) payload =
        match entry.Subject, payload with
        | EntryTarget.Authority _, VerifiedPayload.Authority request ->
            AuthorityFold.apply
                installationId
                (fun id -> Map.containsKey id state.Cases)
                state.Actors
                entry
                request
            |> Result.map (fun actors -> state.Cases, actors)
        | EntryTarget.Case(id, revision, act, _), _ ->
            result {
                let! cases = CaseFold.apply state.Cases entry id revision act payload

                let human =
                    List.contains act [ CaseActKind.Void; CaseActKind.Reinstate; CaseActKind.Erase ]

                do! AuthorityFold.executor state.Actors entry human false
                return cases, state.Actors
            }
        | _ -> Error(divergence (Some entry.Sequence) VerificationFinding.PayloadFormat)

    let link (state: Scan) sequence previous =
        require
            (Some sequence)
            VerificationFinding.Link
            (state.Head.Sequence < Revision.Maximum
             && sequence = state.Head.Sequence + 1L
             && previous = state.Head.EntryHash)

    let append
        installationId
        (expected: ExpectedPosition option)
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

            let! cases, actors = histories installationId state entry payload

            let seen =
                expected
                |> Option.exists (fun position ->
                    position.Sequence = sequence && position.EntryHash = entry.EntryHash)

            return
                {
                    Head =
                        {
                            Sequence = sequence
                            EntryHash = entry.EntryHash
                        }
                    Cases = cases
                    Actors = actors
                    EarliestDuplicate = state.EarliestDuplicate
                    ExpectedSeen = state.ExpectedSeen || seen
                }
        }

    let initial (expected: ExpectedPosition option) earliestDuplicate =
        let head: LedgerHead =
            {
                Sequence = 0L
                EntryHash = Array.zeroCreate 32
            }

        {
            Head = head
            Cases = Map.empty
            Actors = Map.empty
            EarliestDuplicate = earliestDuplicate
            ExpectedSeen =
                expected
                |> Option.exists (fun position ->
                    position.Sequence = 0L && position.EntryHash = head.EntryHash)
        }

    let head (state: Scan) (actual: LedgerHead option) =
        require None VerificationFinding.Head (actual = Some state.Head)

    let complete (expected: ExpectedPosition option) (state: Scan) =
        if expected.IsSome && not state.ExpectedSeen then
            Error(
                divergence (expected |> Option.map _.Sequence) VerificationFinding.ExpectedPosition
            )
        else
            Ok(LedgerEntries.position state.Head.Sequence state.Head.EntryHash)
