namespace ClaimCore.Application


open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Application.VerificationChecks

/// Deterministic ledger interpretation independent of storage, current admission and clocks.
module LedgerVerification =
    type ProfileHistory =
        {
            Missing: Sequence option
            Present: Sequence option
        }

    type PendingEffects =
        {
            Root: StoredEntry
            NextIndex: int
            Nonces: byte array list
        }

    type Scan =
        {
            Head: LedgerPosition
            Cases: Map<CaseId, CaseHistory>
            Work: Map<CaseId, WorkHistory>
            Reminders: Map<ReminderId, ReminderHistory>
            Actors: Map<ActorId, ActorAuthority>
            Fences: Map<ActorId * OperationId, DateTimeOffset>
            Accepted: Map<OperationId, ActorId>
            PendingEffects: PendingEffects option
            Profiles: Map<ActorId, ProfileHistory>
            References: Map<string, CaseId>
            ExpectedSeen: bool
        }

    let private effects pending (entry: StoredEntry) =
        let index = LedgerEffects.index entry.Effects
        let count = LedgerEffects.count entry.Effects

        let result =
            match pending, index with
            | None, 0 when count = 1 -> Some None
            | None, 0 ->
                Some(
                    Some
                        {
                            Root = entry
                            NextIndex = 1
                            Nonces = entry.PayloadNonce |> Option.toList
                        }
                )
            | Some pending, index when index = pending.NextIndex ->
                let root = pending.Root

                let targetMatches =
                    match root.Subject, entry.Subject with
                    | EntryTarget.Authority(target, _), EntryTarget.Authority(current, _) ->
                        target = current
                    | _ -> false

                let fresh =
                    entry.PayloadNonce
                    |> Option.forall (fun nonce -> not (List.contains nonce pending.Nonces))

                if
                    root.OperationId = entry.OperationId
                    && root.ActorId = entry.ActorId
                    && root.RecordedAt = entry.RecordedAt
                    && LedgerEffects.count root.Effects = count
                    && LedgerEffects.authorityOperation root.Effects =
                        LedgerEffects.authorityOperation entry.Effects
                    && Sequence.value entry.Sequence = Sequence.value root.Sequence + int64 index
                    && targetMatches
                    && fresh
                then
                    if index + 1 = count then
                        Some None
                    else
                        Some(
                            Some
                                { pending with
                                    NextIndex = index + 1
                                    Nonces = pending.Nonces @ (entry.PayloadNonce |> Option.toList)
                                }
                        )
                else
                    None
            | _ -> None

        result
        |> Option.map Ok
        |> Option.defaultValue (
            Error(divergence (Some entry.Sequence) VerificationFinding.OperationEffectGroup)
        )

    let private profiles history (entry: StoredEntry) payload =
        match entry.Subject with
        | EntryTarget.Authority(id, AuthorityKind.SetProfile) ->
            let current =
                Map.tryFind id history |> Option.defaultValue { Missing = None; Present = None }

            let next =
                match payload with
                | VerifiedPayload.ErasedProfile ->
                    { current with
                        Missing = Option.orElse (Some entry.Sequence) current.Missing
                    }
                | _ ->
                    { current with
                        Present = Option.orElse (Some entry.Sequence) current.Present
                    }

            Ok(Map.add id next history)
        | EntryTarget.Authority(id, AuthorityKind.EraseProfile) ->
            match Map.tryFind id history |> Option.bind _.Present with
            | Some position ->
                Error(divergence (Some position) VerificationFinding.ErasureIncomplete)
            | None -> Ok(Map.remove id history)
        | _ -> Ok history

    let private histories installationId state (entry: StoredEntry) payload =
        match entry.Subject, payload with
        | EntryTarget.NonAcceptance _, VerifiedPayload.NonAcceptance _ ->
            AuthorityFold.executor state.Actors entry false false
            |> Result.map (fun () -> state.Cases, state.Actors)
        | EntryTarget.Authority _, VerifiedPayload.ErasedProfile ->
            AuthorityFold.erasedProfile state.Actors entry
            |> Result.map (fun actors -> state.Cases, actors)
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
        | (EntryTarget.Work _ | EntryTarget.Reminder _), _ -> Ok(state.Cases, state.Actors)
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
                | EntryTarget.Work subject -> RecordedTarget.Work subject
                | EntryTarget.Reminder subject -> RecordedTarget.Reminder subject
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
                    (metadata.Effects = entry.Effects
                     && metadata.InstallationId = installationId
                     && metadata.Sequence = entry.Sequence
                     && metadata.OperationId = entry.OperationId
                     && metadata.ActorId = entry.ActorId
                     && metadata.RecordedAt = entry.RecordedAt
                     && metadata.Subject = subject
                     && metadata.CreationReference = entry.CreationReference
                     && metadata.PayloadCommitment = entry.PayloadCommitment)

            let! pendingEffects = effects state.PendingEffects entry

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
            let! profiles = profiles state.Profiles entry payload

            let! work, reminders =
                WorkFold.apply state.Cases state.Actors state.Work state.Reminders entry payload

            let seen =
                expected
                |> Option.exists (fun position ->
                    position.Sequence = sequence
                    && EntryHash.toBytes position.EntryHash = entry.EntryHash
                    && pendingEffects.IsNone)

            return
                {
                    Head =
                        {
                            Sequence = sequence
                            EntryHash = EntryHash.ofBytes entry.EntryHash |> Option.get
                        }
                    Work = work
                    Reminders = reminders
                    Cases = cases
                    Actors = actors
                    PendingEffects = pendingEffects
                    Profiles = profiles
                    Accepted =
                        match payload with
                        | VerifiedPayload.NonAcceptance _ -> state.Accepted
                        | _ when pendingEffects.IsNone ->
                            Map.add entry.OperationId entry.ActorId state.Accepted
                        | _ -> state.Accepted
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
            Work = Map.empty
            Reminders = Map.empty
            Cases = Map.empty
            Actors = Map.empty
            Fences = Map.empty
            Accepted = Map.empty
            PendingEffects = None
            Profiles = Map.empty
            References = Map.empty
            ExpectedSeen =
                expected
                |> Option.exists (fun position ->
                    position.Sequence = Sequence.zero && position.EntryHash = head.EntryHash)
        }

    let head (state: Scan) (actual: LedgerPosition option) =
        require None VerificationFinding.Head (actual = Some state.Head)

    let complete (expected: LedgerPosition option) (state: Scan) =
        if state.PendingEffects.IsSome then
            Error(divergence (Some state.Head.Sequence) VerificationFinding.OperationEffectGroup)
        elif expected.IsSome && not state.ExpectedSeen then
            Error(
                divergence (expected |> Option.map _.Sequence) VerificationFinding.ExpectedPosition
            )
        else
            Ok(state.Head)

    let erasure (state: Scan) =
        let missing = state.Profiles |> Map.values |> Seq.choose _.Missing |> Seq.tryHead

        match missing with
        | Some position ->
            Error(divergence (Some position) VerificationFinding.PayloadWithoutErasure)
        | None -> CaseFold.erasure state.Cases
        |> Result.bind (fun () -> WorkFold.erasure state.Cases state.Work state.Reminders)
