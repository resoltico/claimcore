module ClaimCore.IntegrationTests.CaseLifecycleAuditTests

open System
open System.IO
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests

let private mergedReplayAndTamper =
    testCase
        "[CC-AUDIT-001] full audit merges disposition revisions and detects a tampered lifecycle tip"
        (fun _ ->
            setup
                (fun
                    owner
                    (source, _)
                    witness
                    runtime
                    proposer
                    firstApprover
                    secondApprover
                    ungranted
                    _ ->
                    let actor = runtime.ForActor proposer
                    let opened, blocked = voidCase owner actor (runtime.ForActor ungranted)

                    reinstateCase
                        actor
                        [ runtime.ForActor firstApprover; runtime.ForActor secondApprover ]
                        opened
                        blocked

                    executeAccepted actor (next opened 3L Command.Close)
                    use audit = RuntimeDatabase.openConnection source
                    DataAudit.run audit witness CancellationToken.None |> await |> ignore

                    use ownerConnection = new NpgsqlConnection(owner)
                    ownerConnection.Open()

                    use tamper =
                        new NpgsqlCommand(
                            "UPDATE claimcore.cases SET lifecycle_event_hash="
                            + "decode(repeat('ff',32),'hex') WHERE case_reference=@reference",
                            ownerConnection
                        )

                    Sql.text tamper "reference" opened.CaseReference
                    Expect.equal (tamper.ExecuteNonQuery()) 1 "One synthetic tip was altered"

                    Expect.throwsT<InvalidDataException>
                        (fun () ->
                            DataAudit.run audit witness CancellationToken.None |> await |> ignore)
                        "A primary-owner rewrite cannot forge the witnessed lifecycle tip"))

let tests = testList "case lifecycle full audit" [ mergedReplayAndTamper ]
