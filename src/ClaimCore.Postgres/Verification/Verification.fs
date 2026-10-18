namespace ClaimCore.Postgres

open System
open System.Threading
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Postgres.VerificationChecks
open Npgsql

/// Deterministic precedence: ledger order with link/hash/commitment/format checks first, then
/// history and permanent authority rules; erasure, projections, head and checkpoint follow.
module internal Verification =
    type private Scan =
        {
            Head: LedgerHead
            Cases: Map<Guid, CaseHistory>
            Actors: Map<Guid, ActorAuthority>
            Operations: Set<Guid>
            ExpectedSeen: bool
        }

    let private histories installationId state (entry: StoredEntry) payload =
        match entry.Scope, payload with
        | StoredScope.Authority _, VerifiedPayload.Authority request ->
            AuthorityFold.apply
                installationId
                (fun id -> Map.containsKey id state.Cases)
                state.Actors
                entry
                request
            |> Result.map (fun actors -> state.Cases, actors)
        | StoredScope.Case(id, revision, act, _), _ ->
            result {
                let! cases = CaseFold.apply state.Cases entry id revision act payload

                let human =
                    List.contains
                        act
                        [
                            (CaseActKind.Stewardship StewardshipActKind.Void)
                            (CaseActKind.Stewardship StewardshipActKind.Reinstate)
                            (CaseActKind.Stewardship StewardshipActKind.Erase)
                        ]

                do! AuthorityFold.executor state.Actors entry human false
                return cases, state.Actors
            }
        | _ -> Error(divergence (Some entry.Sequence) VerificationFinding.PayloadFormat)

    let private scanEntry installationId (expected: ExpectedPosition option) (state: Scan) row =
        result {
            let! sequence =
                capture None VerificationFinding.PayloadFormat (fun () ->
                    DatabaseSession.value<int64> 0 row)

            let at = Some sequence

            let! previous =
                capture at VerificationFinding.PayloadFormat (fun () ->
                    DatabaseSession.value<byte array> 1 row)

            do!
                require
                    at
                    VerificationFinding.Link
                    (state.Head.Sequence < Revision.Maximum
                     && sequence = state.Head.Sequence + 1L
                     && previous = state.Head.EntryHash)

            let! raw =
                capture at VerificationFinding.PayloadFormat (fun () ->
                    StoredEntries.decode installationId row)

            let entry = raw.Entry

            let! hash =
                capture at VerificationFinding.PayloadFormat (fun () ->
                    LedgerEntry.hash raw.Metadata)

            do! require at VerificationFinding.EntryHash (hash = entry.EntryHash)
            let! payload = VerificationPayload.read installationId entry

            do!
                require
                    at
                    VerificationFinding.PayloadFormat
                    (not (Set.contains entry.OperationId state.Operations))

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
                    Operations = Set.add entry.OperationId state.Operations
                    ExpectedSeen = state.ExpectedSeen || seen
                }
        }

    let private scan
        connection
        installationId
        (expected: ExpectedPosition option)
        (token: CancellationToken)
        =
        task {
            let head: LedgerHead =
                {
                    Sequence = 0L
                    EntryHash = Array.zeroCreate 32
                }

            let seen =
                expected
                |> Option.exists (fun position ->
                    position.Sequence = 0L && position.EntryHash = head.EntryHash)

            let mutable state =
                Ok
                    {
                        Head = head
                        Cases = Map.empty
                        Actors = Map.empty
                        Operations = Set.empty
                        ExpectedSeen = seen
                    }

            use command =
                DatabaseSession.command
                    connection
                    ($"SELECT {StoredEntries.columns} FROM claimcore.ledger ORDER BY sequence")
                    [||]

            use! reader = command.ExecuteReaderAsync(token)
            let mutable reading = true

            while reading && Result.isOk state do
                let! found = reader.ReadAsync(token)

                if not found then
                    reading <- false
                else
                    let row = Array.zeroCreate<obj> reader.FieldCount
                    reader.GetValues row |> ignore

                    state <-
                        state
                        |> Result.bind (fun state -> scanEntry installationId expected state row)

            return state
        }

    let private head connection (state: Scan) token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT sequence,entry_hash FROM claimcore.ledger_head"
                    [||]
                    token

            return
                capture None VerificationFinding.Head (fun () ->
                    match rows with
                    | [ row ] ->
                        DatabaseSession.value<int64> 0 row = state.Head.Sequence
                        && DatabaseSession.value<byte array> 1 row = state.Head.EntryHash
                    | _ -> false)
                |> Result.bind (require None VerificationFinding.Head)
        }

    let private projections connection installationId (state: Scan) token =
        task {
            let! cases = ProjectionVerification.cases connection installationId state.Cases token

            match cases with
            | Error error -> return Error error
            | Ok() -> return! ProjectionVerification.authority connection state.Actors token
        }

    let private finish
        connection
        installationId
        (expected: ExpectedPosition option)
        (state: Scan)
        token
        =
        task {
            match CaseFold.erasure state.Cases with
            | Error error -> return VerificationResult.Diverged error
            | Ok() ->
                let! projections = projections connection installationId state token

                match projections with
                | Error error -> return VerificationResult.Diverged error
                | Ok() ->
                    let! checkedHead = head connection state token

                    return
                        match checkedHead with
                        | Error error -> VerificationResult.Diverged error
                        | Ok() when expected.IsSome && not state.ExpectedSeen ->
                            VerificationResult.Diverged(
                                divergence
                                    (expected |> Option.map _.Sequence)
                                    VerificationFinding.ExpectedPosition
                            )
                        | Ok() ->
                            VerificationResult.Verified(
                                LedgerEntries.position state.Head.Sequence state.Head.EntryHash
                            )
        }

    let verify
        (source: NpgsqlDataSource)
        (expected: ExpectedPosition option)
        (token: CancellationToken)
        =
        if expected |> Option.exists (ExpectedPositions.isValid >> not) then
            invalidArg (nameof expected) "Expected position is outside the supported format."

        DatabaseSession.read source token (fun connection ->
            task {
                ServerSupport.require connection.PostgreSqlVersion
                let! count = Migrations.history connection Migrations.scripts token

                if count <> Migrations.scripts.Length then
                    invalidOp "Unsupported schema version."

                let! installationId, _ = Migrations.installation connection token
                let! result = scan connection installationId expected token

                match result with
                | Error error -> return VerificationResult.Diverged error
                | Ok state -> return! finish connection installationId expected state token
            })
