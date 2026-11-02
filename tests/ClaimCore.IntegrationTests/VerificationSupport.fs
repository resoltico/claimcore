module internal ClaimCore.IntegrationTests.VerificationSupport

open ClaimCore.TestSupport

open ClaimCore.Protocol

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
            RegisterMaintenanceStore.change database.OwnerSource MaintenanceChange.Enter token

        Expect.isOk mode "The owner explicitly enters register-write maintenance"
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
            | VerificationResult.Diverged _ ->
                failtest "Synthetic history must verify before tampering"
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
                    request 10 "synthetic-a" 0L (AuthoredCaseAct.Open registration)
                ))
                (token))

        accepted opened |> ignore
        let! _ = verified database
        return actor
    }

let row (database: PostgresFixture.Database) (sequence: Sequence) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                ($"SELECT {StoredEntries.columns} FROM claimcore.ledger WHERE sequence=$1")
                [| box (Sequence.value sequence) |]
                token

        return StoredEntries.decode database.Fixture.InstallationId (List.exactlyOne rows)
    }

type private RecordedSubjectColumns =
    {
        Kind: string
        Act: string
        Id: Guid
        Revision: int64 option
        BusinessDate: DateOnly option
    }

let private subject =
    function
    | RecordedTarget.NonAcceptance operation ->
        {
            Kind = "CONTROL"
            Act = "NOT_ACCEPTED"
            Id = OperationId.value operation
            Revision = None
            BusinessDate = None
        }
    | RecordedTarget.Case value ->
        {
            Kind = "CASE"
            Act = CaseActKind.token value.Act
            Id = (CaseId.value (value.CaseId))
            Revision = Some(Revision.value (value.Revision))
            BusinessDate = Some value.BusinessDate
        }
    | RecordedTarget.Authority value ->
        {
            Kind = "AUTHORITY"
            Act = AuthorityKind.token value.Act
            Id = (ActorId.value (value.TargetActorId))
            Revision = None
            BusinessDate = None
        }

let private updateSql =
    "UPDATE claimcore.ledger SET previous_hash=$2,entry_hash=$3,subject_kind=$4,act=$5,operation_id=$6,actor_id=$7,recorded_at=$8,subject_id=$9,case_revision=$10,business_date=$11,payload_commitment=$12,payload=$13,payload_nonce=$14,creation_reference=$15 WHERE sequence=$1"

let private parameters (value: LedgerRow) =
    let metadata = value.Metadata
    let target = subject metadata.Subject
    let kind = target.Kind
    let act = target.Act
    let subject = target.Id
    let revision = target.Revision
    let date = target.BusinessDate

    [|
        box (Sequence.value metadata.Sequence)
        box metadata.PreviousHash
        box (LedgerEntry.hash metadata)
        box kind
        box act
        box (OperationId.value metadata.OperationId)
        box (ActorId.value metadata.ActorId)
        box metadata.RecordedAt
        box subject
        DatabaseSession.parameter revision
        DatabaseSession.parameter date
        box metadata.PayloadCommitment
        DatabaseSession.parameter value.Entry.Payload
        DatabaseSession.parameter value.Entry.PayloadNonce
        DatabaseSession.parameter (metadata.CreationReference |> Option.map CaseReference.format)
    |]

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

/// Rewrite only owned synthetic evidence, with guard rejection first and re-linking so another
/// damaged link cannot mask the intended structural or permanent-authority finding.
let rewrite (database: PostgresFixture.Database) sequence mutate =
    task {
        let! original = row database sequence
        let first = mutate original
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let mutable refused = false

        try
            let! _ = DatabaseSession.execute connection updateSql (parameters first) token in ()
        with :? PostgresException as error ->
            refused <- error.SqlState = "CC003"

        Expect.isTrue refused "Guard rejects the intended metadata/payload rewrite"
        use! transaction = connection.BeginTransactionAsync()

        let! _ =
            DatabaseSession.execute
                connection
                "SET LOCAL session_replication_role=replica"
                [||]
                token

        let! rows =
            DatabaseSession.rows
                connection
                ($"SELECT {StoredEntries.columns} FROM claimcore.ledger WHERE sequence>=$1 ORDER BY sequence")
                [| box (Sequence.value sequence) |]
                token

        let mutable previous = None

        for raw in rows do
            let current = StoredEntries.decode database.Fixture.InstallationId raw
            let current = if current.Entry.Sequence = sequence then first else current

            let metadata =
                { current.Metadata with
                    PreviousHash = previous |> Option.defaultValue current.Metadata.PreviousHash
                }

            let current = { current with Metadata = metadata }
            let! _ = DatabaseSession.execute connection updateSql (parameters current) token
            previous <- Some(LedgerEntry.hash metadata)

        let! _ =
            DatabaseSession.execute
                connection
                "UPDATE claimcore.ledger_head SET entry_hash=$1"
                [| box (Option.get previous) |]
                token

        do! transaction.CommitAsync()
    }

let payload request snapshot (value: LedgerRow) =
    let bytes =
        LedgerEntry.encodePayload
            {
                Request = request
                Snapshot = snapshot
            }

    let nonce =
        value.Entry.PayloadNonce
        |> Option.defaultWith (fun () -> failtest "Retained fixture nonce is required")

    { value with
        Entry =
            { value.Entry with
                Payload = Some bytes
            }
        Metadata =
            { value.Metadata with
                PayloadCommitment = LedgerEntry.commitment nonce bytes
            }
    }

let caseSubject change (value: LedgerRow) =
    match value.Metadata.Subject with
    | RecordedTarget.Case current ->
        { value with
            Metadata =
                { value.Metadata with
                    Subject = RecordedTarget.Case(change current)
                }
        }
    | _ -> failtest "Case fixture is required"

let authoritySubject change (value: LedgerRow) =
    match value.Metadata.Subject with
    | RecordedTarget.Authority current ->
        { value with
            Metadata =
                { value.Metadata with
                    Subject = RecordedTarget.Authority(change current)
                }
        }
    | _ -> failtest "Authority fixture is required"
