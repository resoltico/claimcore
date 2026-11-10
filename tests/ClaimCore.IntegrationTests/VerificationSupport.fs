module internal ClaimCore.IntegrationTests.VerificationSupport

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreFixtures

let token = CancellationToken.None

let run action =
    async {
        let! database = PostgresFixture.create () |> Async.AwaitTask

        try
            do! action database |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

let sql (database: PostgresFixture.Database) statement parameters =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let! _ = DatabaseSession.execute connection statement parameters token
        return ()
    }

let enterMaintenance (database: PostgresFixture.Database) =
    task {
        let! mode =
            RegisterMaintenanceStore.change database.OwnerSource MaintenanceChange.On token

        Expect.isTrue mode.State.Active "The owner explicitly pauses register writes"
    }

let position =
    function
    | VerificationResult.Verified position -> position
    | VerificationResult.Diverged _ -> failtest "Expected verified position"

let verified (database: PostgresFixture.Database) =
    task {
        let! result = Verification.verify database.OwnerSource None token

        return
            match result with
            | VerificationResult.Verified position -> position
            | VerificationResult.Diverged failure ->
                failtestf
                    "Synthetic history must verify before tampering: %A at %A"
                    failure.Finding
                    failure.Sequence
    }

let finding expected (database: PostgresFixture.Database) =
    task {
        let! result = Verification.verify database.OwnerSource None token

        match result with
        | VerificationResult.Diverged value ->
            Expect.equal value.Finding expected "Exact typed finding"
        | _ -> failtest "Tampering was not detected"
    }

let populated (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let! opened =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request database.Fixture 10 "synthetic-a" 0L (AuthoredCaseAct.Open registration)
                ))
                (token))

        accepted opened |> ignore
        let! _ = verified database
        return actor
    }

let guardedSql (database: PostgresFixture.Database) statement parameters =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let mutable refused = false

        try
            let! _ = DatabaseSession.execute connection statement parameters token in ()
        with :? PostgresException as error ->
            refused <- error.SqlState = "CC003"

        Expect.isTrue refused "The owner-level append-only guard rejects the same write"
        use! transaction = connection.BeginTransactionAsync()

        let! _ =
            DatabaseSession.execute
                connection
                "SET LOCAL session_replication_role=replica"
                [||]
                token

        let! _ = DatabaseSession.execute connection statement parameters token
        do! transaction.CommitAsync()
    }

/// Read the real accepted closure; tests do not reconstruct a parallel ledger-row model.
let row (database: PostgresFixture.Database) (sequence: Sequence) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT operation_id FROM claimcore.ledger WHERE sequence=$1"
                [| box (Sequence.value sequence) |]
                token

        let identity =
            DatabaseSession.value<Guid> 0 (List.exactlyOne rows)
            |> OperationId.create
            |> Option.get

        let! operation =
            OperationStorage.find connection database.Fixture.InstallationId identity token

        return operation |> Option.defaultWith (fun () -> failtest "Accepted root is missing")
    }

let private updateRoot =
    "UPDATE claimcore.ledger SET previous_hash=$2,entry_hash=$3,installation_format=$4,operation_id=$5,actor_id=$6,recorded_at=$7,business_date=$8,command=$9::jsonb WHERE sequence=$1"

let private rootParameters root =
    let parameters = OperationStorage.parameters root
    Array.append [| parameters[1] |] parameters[2..9]

let private replaceRoot connection root =
    task {
        let parameters = OperationStorage.parameters root
        let! _ = DatabaseSession.execute connection updateRoot (rootParameters root) token

        let! _ =
            DatabaseSession.execute
                connection
                "DELETE FROM claimcore.ledger_effects WHERE operation_sequence=$1"
                [| parameters[1] |]
                token

        let! _ =
            DatabaseSession.execute
                connection
                "INSERT INTO claimcore.ledger_effects(operation_sequence,effect_index,descriptor) SELECT $1,(value->>'index')::integer,value->'descriptor' FROM jsonb_array_elements($2::jsonb)"
                [| parameters[1]; parameters[10] |]
                token

        let! _ =
            DatabaseSession.execute
                connection
                "DELETE FROM claimcore.ledger_compartments WHERE operation_sequence=$1"
                [| parameters[1] |]
                token

        let! _ =
            DatabaseSession.execute
                connection
                "INSERT INTO claimcore.ledger_compartments(operation_sequence,ordinal,owner_kind,owner_id,purpose,effect_indices,commitment,nonce,payload) SELECT $1,(value->>'ordinal')::integer,value->>'ownerKind',(value->>'ownerId')::uuid,value->>'purpose',ARRAY(SELECT jsonb_array_elements_text(value->'effects')::integer),decode(value->>'commitment','hex'),decode(value->>'nonce','hex'),decode(value->>'payload','hex') FROM jsonb_array_elements($2::jsonb)"
                [| parameters[1]; parameters[11] |]
                token

        return ()
    }

/// Tamper only an owned fixture, proving the live guard first. Rehash subsequent roots so
/// a broken link cannot conceal the independently expected semantic replay failure.
let rewrite
    (database: PostgresFixture.Database)
    sequence
    (mutate: RetainedOperation -> OperationRoot)
    =
    task {
        let! original = row database sequence
        let changed = mutate original
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let mutable refused = false

        try
            let! _ = DatabaseSession.execute connection updateRoot (rootParameters changed) token
            ()
        with :? PostgresException as error ->
            refused <- error.SqlState = "CC003"

        Expect.isTrue refused "The real append-only guard refuses the intended root rewrite"

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT operation_id FROM claimcore.ledger WHERE sequence>=$1 ORDER BY sequence"
                [| box (Sequence.value sequence) |]
                token

        let originals = ResizeArray<RetainedOperation>()

        for raw in rows do
            let identity = DatabaseSession.value<Guid> 0 raw |> OperationId.create |> Option.get

            let! value =
                OperationStorage.find connection database.Fixture.InstallationId identity token

            originals.Add(Option.get value)

        use! transaction = connection.BeginTransactionAsync()

        let! _ =
            DatabaseSession.execute
                connection
                "SET LOCAL session_replication_role=replica"
                [||]
                token

        let mutable previous = None

        for original in originals do
            let root =
                if original.Root.Header.Sequence = sequence then
                    changed
                else
                    original.Root

            let header =
                { root.Header with
                    PreviousHash = previous |> Option.defaultValue root.Header.PreviousHash
                }

            let rewritten =
                OperationRoot.append
                    header
                    root.Structure.Command
                    root.Structure.Effects
                    root.Structure.Compartments
                |> Result.defaultWith (fun error -> failtestf "Synthetic root rewrite: %A" error)

            do! replaceRoot connection rewritten
            previous <- Some rewritten.Hash

        let! _ =
            DatabaseSession.execute
                connection
                "UPDATE claimcore.ledger_head SET entry_hash=$1"
                [| box (EntryHash.toBytes (Option.get previous)) |]
                token

        do! transaction.CommitAsync()
    }

let recreate header command values nonce =
    RetainedOperation.create header command values (fun () -> nonce)
    |> Result.defaultWith (fun error -> failtestf "Synthetic retained values: %A" error)
    |> _.Root

/// Capture the installation/frontier checkpoint through the actual owner API after verification.
let checkpoint (database: PostgresFixture.Database) =
    task {
        let! expected = verified database
        let! current = LedgerAdministration.head database.OwnerSource token
        Expect.equal current.Position expected "Checkpoint captures the independently verified root"
        return current
    }
