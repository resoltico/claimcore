module ClaimCore.IntegrationTests.LedgerGroupingTests

open System
open Expecto
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.IntegrationTests.VerificationSupport

let private structuralGroups (database: PostgresFixture.Database) =
    task {
        let op = CaseFixtures.operationId (OperationIds.fresh ())

        let actor = CaseFixtures.actorId (Guid.Parse "a0000000-0000-4000-8000-000000000099")

        let entries head instant =
            let mutable nonce = 0uy

            LedgerEntries.createAuthorityOperation
                AuthorityKind.Register
                (fun () ->
                    nonce <- nonce + 1uy
                    Array.create 32 nonce)
                {
                    InstallationId = database.Fixture.InstallationId
                    Head = head
                    Instant = instant
                    ActorId = actor
                    OperationId = op
                    Effects =
                        [ AuthorityKind.Register; AuthorityKind.SetProfile ]
                        |> List.map (fun kind ->
                            {
                                Subject = EntryTarget.Authority(actor, kind)
                                Request = [| 1uy |]
                                Snapshot = Array.empty
                            })
                }
            |> Result.defaultWith (fun error -> failtestf "Structural witness failed: %A" error)

        let cases =
            [
                "truncated", (fun (rows: EntryDraft list) -> [ rows[0] ])
                "mixed count",
                (fun rows ->
                    [
                        rows[0]
                        { rows[1] with
                            Effects = LedgerEffects.authority AuthorityKind.SetProfile 1 0
                        }
                    ])
                "mixed actor",
                (fun rows ->
                    [
                        rows[0]
                        { rows[1] with
                            ActorId = CaseFixtures.actorId (Guid.NewGuid())
                        }
                    ])
                "mixed target",
                (fun rows ->
                    [
                        rows[0]
                        { rows[1] with
                            Subject =
                                EntryTarget.Authority(
                                    CaseFixtures.actorId (Guid.NewGuid()),
                                    AuthorityKind.Grant
                                )
                        }
                    ])
                "mixed operation kind",
                (fun rows ->
                    [
                        rows[0]
                        { rows[1] with
                            Effects = LedgerEffects.authority AuthorityKind.ProvisionOwner 2 1
                        }
                    ])
                "NULL operation kind",
                (fun rows ->
                    [
                        { rows[0] with
                            Effects = LedgerEffects.single
                        }
                    ])
                "reused nonce",
                (fun rows ->
                    [
                        rows[0]
                        { rows[1] with
                            PayloadNonce = rows[0].PayloadNonce
                        }
                    ])
            ]

        for name, alter in cases do
            let! rejected =
                task {
                    use! connection = database.OwnerSource.OpenConnectionAsync()
                    use! transaction = connection.BeginTransactionAsync token
                    let! head, instant = Transactions.head connection token

                    try
                        let rows = entries head instant

                        if name = "mixed count" then
                            let! _ = Persistence.entry connection rows[0] token
                            // Bypass the validated descriptor factory to challenge SQL's physical count check.
                            let! _ =
                                DatabaseSession.execute
                                    connection
                                    "INSERT INTO claimcore.ledger(sequence,previous_hash,entry_hash,installation_format,subject_kind,act,operation_id,actor_id,recorded_at,subject_id,payload_commitment,payload,payload_nonce,effect_index,effect_count,authority_operation) SELECT sequence+1,entry_hash,$1,installation_format,subject_kind,'SET_PROFILE',operation_id,actor_id,recorded_at,subject_id,payload_commitment,payload,$2,1,1,authority_operation FROM claimcore.ledger WHERE operation_id=$3"
                                    [|
                                        box rows[1].EntryHash
                                        box rows[1].PayloadNonce
                                        box (OperationId.value op)
                                    |]
                                    token

                            ()
                        else
                            for entry in alter rows do
                                let! _ = Persistence.entry connection entry token
                                ()

                        do! transaction.CommitAsync token
                        return None
                    with :? PostgresException as error ->
                        return Some error.SqlState
                }

            Expect.isTrue
                (rejected = Some "CC004" || rejected = Some "23514")
                (sprintf "%s must fail the group or shape constraint: %A" name rejected)

            let! missing =
                database.Fixture.Store.Read
                    {
                        Work = (fun tx -> tx.FindOperation op)
                        CancellationToken = token
                    }

            Expect.equal missing (Ok None) "Rejected group leaves no root or child"

        let write action =
            database.Fixture.Store.Write
                {
                    Work = action
                    CancellationToken = token
                }

        for stopAfter in [ 1; 2 ] do
            let! failed =
                write (fun tx ->
                    task {
                        for entry in entries tx.Head tx.ObservedAt |> List.take stopAfter do
                            do! tx.Append entry

                        return failwith "injected group rollback"
                    })

            Expect.equal
                failed
                (Error StoreFailure.Internal: Result<Verdict<unit>, StoreFailure>)
                "Injected callback rolls every append back"

            let! missing =
                database.Fixture.Store.Read
                    {
                        Work = (fun tx -> tx.FindOperation op)
                        CancellationToken = token
                    }

            Expect.equal missing (Ok None) "Rollback leaves no accepted prefix"

        let! written =
            write (fun tx ->
                task {
                    let rows = entries tx.Head tx.ObservedAt

                    for entry in rows do
                        do! tx.Append entry

                    return Verdict.Proceed rows
                })

        let rows =
            match written with
            | Ok(Verdict.Proceed rows) -> rows
            | other -> failtestf "Complete SQL witness refused: %A" other

        let! loaded =
            database.Fixture.Store.Read
                {
                    Work = (fun tx -> tx.FindOperation op)
                    CancellationToken = token
                }

        match loaded with
        | Ok(Some group) ->
            Expect.equal group.Entries.Length 2 "Bounded root and effects lookup"
            Expect.equal group.Position.Sequence rows[1].Sequence "Final effect owns proof"
        | other -> failtestf "Complete SQL witness missing: %A" other
    }

[<Tests>]
let tests =
    testList
        "ledger operation grouping SQL"
        [
            testCaseAsync
                "deferred completeness NULL coherence independent salts and rollback"
                (run structuralGroups)
        ]
