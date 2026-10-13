namespace ClaimCore.Postgres

open System
open System.Threading
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open Npgsql

/// A position independently recorded by an operator or returned in a receipt.
type ExpectedPosition =
    {
        Sequence: int64
        EntryHash: byte array
    }

module internal Verification =
    type private CaseFold =
        {
            Revision: int64
            Snapshot: CaseView option
            Disposition: CaseDisposition
            Erased: bool
            MissingPayload: bool
            PresentPayload: bool
        }

    let private failUnless condition message =
        if not condition then
            invalidOp message

    let private caseEntry (folds: Map<Guid, CaseFold>) (entry: StoredEntry) =
        match entry.Scope with
        | StoredScope.Authority _ -> invalidOp "Expected case entry."
        | StoredScope.Case(id, revision, act, _) ->
            let previous = Map.tryFind id folds
            let expected = previous |> Option.map _.Revision |> Option.defaultValue 0L

            failUnless
                (revision = expected + 1L && not (previous |> Option.exists _.Erased))
                "Case revisions are not contiguous."

            failUnless
                (revision <> 1L || act = AcceptedAct.Command CommandKind.Open)
                "Case history does not start with OPEN."

            let erased = act = AcceptedAct.Stewardship StewardshipKind.Erase

            let snapshot =
                match entry.Payload with
                | None -> None
                | Some _ ->
                    LedgerEntries.receipt entry
                    |> Result.defaultWith (fun _ -> invalidOp "Case payload is invalid.")
                    |> _.Snapshot

            let disposition =
                match act with
                | AcceptedAct.Stewardship StewardshipKind.Void -> CaseDisposition.Voided
                | AcceptedAct.Stewardship StewardshipKind.Reinstate -> CaseDisposition.Active
                | _ ->
                    previous
                    |> Option.map _.Disposition
                    |> Option.defaultValue CaseDisposition.Active

            Map.add
                id
                {
                    Revision = revision
                    Snapshot = snapshot
                    Disposition = disposition
                    Erased = erased
                    MissingPayload =
                        entry.Payload.IsNone || (previous |> Option.exists _.MissingPayload)
                    PresentPayload =
                        entry.Payload.IsSome || (previous |> Option.exists _.PresentPayload)
                }
                folds

    let private projections installationId (cases: StoredCase list) folds =
        failUnless (cases.Length = Map.count folds) "Case projection count differs."

        for case in cases do
            failUnless
                (CaseReferenceId.encode installationId case.Reference = case.CaseId)
                "Case reference differs from its ledger identity."

            let fold =
                Map.tryFind case.CaseId folds
                |> Option.defaultWith (fun () -> invalidOp "Case projection has no history.")

            failUnless (case.Revision = fold.Revision) "Case projection revision differs."

            match case.State with
            | StoredCaseState.Erased ->
                failUnless
                    (fold.Erased && fold.MissingPayload)
                    "Erased projection has no erased history."
            | StoredCaseState.Live(snapshot, disposition) ->
                failUnless
                    (not fold.Erased
                     && not fold.MissingPayload
                     && fold.Snapshot = Some snapshot
                     && fold.Disposition = disposition)
                    "Case projection differs from its snapshot."

    let private actorProjections connection actors token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT principal_kind,issuer,subject FROM claimcore.actors ORDER BY actor_id"
                    [||]
                    token

            failUnless (rows.Length = Map.count actors) "Actor projection count differs."

            for row in rows do
                let principal =
                    PrincipalKey.fromStorage
                        (DatabaseSession.value 0 row)
                        (DatabaseSession.value 1 row)
                        (DatabaseSession.value 2 row)
                    |> Result.defaultWith (fun _ -> invalidOp "Invalid projected principal.")

                let! stored = Projections.actor connection principal token
                let stored = stored |> Option.defaultWith (fun () -> invalidOp "Actor disappeared.")

                let expected =
                    Map.tryFind stored.ActorId actors
                    |> Option.defaultWith (fun () -> invalidOp "Actor projection has no history.")

                failUnless
                    ({ stored with Grants = [] } = { expected with Grants = [] }
                     && Set.ofList stored.Grants = Set.ofList expected.Grants)
                    "Authority projection differs from its fold."
        }

    type private Scan =
        {
            Head: LedgerHead
            Cases: Map<Guid, CaseFold>
            Actors: Map<Guid, ActorAuthority>
            ExpectedSeen: bool
        }

    let private scanEntry installationId cases expected state row =
        let raw = StoredEntries.decode installationId Guid.Empty row
        let entry = raw.Entry

        failUnless
            (entry.Sequence = state.Head.Sequence + 1L
             && raw.Metadata.PreviousHash = state.Head.EntryHash)
            "Ledger linkage differs."

        failUnless (LedgerEntry.hash raw.Metadata = entry.EntryHash) "Ledger metadata hash differs."

        let actors =
            match entry.Payload, raw.PayloadNonce, entry.RequestSha256 with
            | Some payload, Some nonce, Some _ ->
                failUnless
                    (LedgerEntry.commitment nonce payload = raw.Metadata.PayloadCommitment)
                    "Ledger payload commitment differs."

                let payload =
                    LedgerEntries.payload entry
                    |> Result.defaultWith (fun _ -> invalidOp "Ledger request digest differs.")

                match entry.Scope with
                | StoredScope.Authority _ -> AuthorityFold.apply cases state.Actors entry payload
                | StoredScope.Case _ -> state.Actors
            | None, None, None ->
                failUnless
                    (match entry.Scope with
                     | StoredScope.Case _ -> true
                     | _ -> false)
                    "Authority payload is absent."

                state.Actors
            | _ -> invalidOp "Ledger payload columns differ."

        let folds =
            match entry.Scope with
            | StoredScope.Case _ -> caseEntry state.Cases entry
            | _ -> state.Cases

        let seen =
            expected
            |> Option.exists (fun position ->
                position.Sequence = entry.Sequence && position.EntryHash = entry.EntryHash)

        {
            Head =
                {
                    Sequence = entry.Sequence
                    EntryHash = entry.EntryHash
                }
            Cases = folds
            Actors = actors
            ExpectedSeen = state.ExpectedSeen || seen
        }

    let private scan connection installationId cases expected (token: CancellationToken) =
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
                {
                    Head = head
                    Cases = Map.empty
                    Actors = Map.empty
                    ExpectedSeen = seen
                }

            use command =
                DatabaseSession.command
                    connection
                    ($"SELECT {StoredEntries.columns} FROM claimcore.ledger ORDER BY sequence")
                    [||]

            use! reader = command.ExecuteReaderAsync(token)
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(token)

                if not found then
                    reading <- false
                else
                    let row = Array.zeroCreate<obj> reader.FieldCount
                    reader.GetValues row |> ignore
                    state <- scanEntry installationId cases expected state row

            return state
        }

    let verify (source: NpgsqlDataSource) expected (token: CancellationToken) =
        DatabaseSession.read source token (fun connection ->
            task {
                let! count = Migrations.history connection Migrations.scripts token
                failUnless (count = Migrations.scripts.Length) "Unsupported schema version."
                let! installationId, _ = Migrations.installation connection token

                let! rows =
                    DatabaseSession.rows
                        connection
                        ($"SELECT {Projections.caseColumns} FROM claimcore.cases")
                        [||]
                        token

                let cases = rows |> List.map Projections.caseRow

                for row in rows do
                    if DatabaseSession.value<string> 3 row = "ERASED" then
                        failUnless
                            (row[4..] |> Array.forall Convert.IsDBNull)
                            "Erased projection retains business values."

                let! state = scan connection installationId cases expected token

                for KeyValue(_, fold) in state.Cases do
                    failUnless
                        (fold.Erased || not fold.MissingPayload)
                        "Payload is absent without an accepted erasure."

                    failUnless
                        (not fold.Erased || not fold.PresentPayload)
                        "Erased history retains a payload."

                projections installationId cases state.Cases
                do! actorProjections connection state.Actors token

                let! heads =
                    DatabaseSession.rows
                        connection
                        "SELECT sequence,entry_hash FROM claimcore.ledger_head"
                        [||]
                        token

                failUnless
                    (heads.Length = 1
                     && DatabaseSession.value<int64> 0 heads.Head = state.Head.Sequence
                     && DatabaseSession.value<byte array> 1 heads.Head = state.Head.EntryHash)
                    "Ledger head differs."

                failUnless
                    (expected.IsNone || state.ExpectedSeen)
                    "Expected ledger position is absent or differs."

                return state.Head
            })
