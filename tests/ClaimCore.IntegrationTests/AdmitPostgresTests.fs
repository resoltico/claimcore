module ClaimCore.IntegrationTests.AdmitPostgresTests

open Expecto
open System
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.TestSupport
open ClaimCore.IntegrationTests.VerificationSupport

let private withVerifiedLedger scenario (database: PostgresFixture.Database) =
    task {
        do! scenario database.Fixture
        let! _ = verified database
        return ()
    }

let private erasedRebuild (database: PostgresFixture.Database) =
    task {
        do! AdmitContract.lifecycle database.Fixture
        let! before = checkpoint database

        do!
            sql
                database
                "UPDATE claimcore.actors SET display_name='Resurrected projection',display_name_search='resurrected projection',contact='Wrong contact',contact_search='wrong contact' WHERE actor_id=$1"
                [| box (id database.Fixture 740) |]

        do! finding VerificationFinding.PeopleProfileProjection database
        do! enterMaintenance database
        let! rebuilt = ProjectionRebuild.rebuild database.OwnerSource before token

        Expect.isOk
            rebuilt
            "Verified admission effects rebuild without resurrecting an erased profile"

        let! after = verified database
        Expect.equal after before.Position "Rebuild preserves the whole operation's final proof"

        let! person =
            database.Fixture.Store.Read
                {
                    CancellationToken = token
                    Work = (fun tx -> tx.FindActor(CaseFixtures.actorId (id database.Fixture 740)))
                }

        let person =
            person
            |> Result.defaultWith (fun error -> failtestf "Restored actor: %A" error)
            |> Option.get

        Expect.isNone person.Profile "Only live post-erasure profile payloads may rebuild a name"

        let! grants =
            Service.grants
                (core database.Fixture database.Fixture.Owner)
                {
                    ActorId = id database.Fixture 740
                    AfterCursor = None
                    Limit = 50
                }
                token

        match grants with
        | Outcome.Succeeded page ->
            Expect.equal
                page.Items.Length
                3
                "Profile erasure/rebuild retains every independent grant"
        | other -> failtestf "Restored grant read: %A" other
    }

let private duplicateHistory (database: PostgresFixture.Database) =
    task {
        let! _ = bootstrap database.Fixture
        let owner = core database.Fixture database.Fixture.Owner

        let principal =
            PrincipalKey.human "https://issuer.example/" "duplicate-retained-admission"
            |> Result.defaultWith (string >> failtest)

        let! admitted =
            Service.execute
                owner
                (OperationInput.People(
                    peopleRequest
                        (id database.Fixture 930)
                        principal
                        (PeopleAct.Admit
                            {
                                Profile = Inputs.actorProfile
                                Grants =
                                    [
                                        {
                                            Role = Role.CaseReader
                                            Scope = GrantTarget.Installation
                                        }
                                        {
                                            Role = Role.CaseHandler
                                            Scope = GrantTarget.Installation
                                        }
                                    ]
                            })
                ))
                token

        accepted admitted |> ignore
        let! _ = verified database

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger SET command=jsonb_set(command,'{act,grants,1}',command#>'{act,grants,0}') WHERE operation_id=$1"
                [| box (id database.Fixture 930) |]

        do! finding VerificationFinding.PayloadFormat database
    }

[<Tests>]
let tests =
    testList
        "atomic people admission"
        [
            testCaseAsync
                "erased admission rebuild preserves all grants without reviving names"
                (run erasedRebuild)
            testCaseAsync
                "retained duplicate admission grants are rejected by the structural grammar"
                (run duplicateHistory)
            testCaseAsync
                "every effect/projection failure restores identity grants and waiting hints"
                (run (withVerifiedLedger AdmitContract.atomicWrites))
            testCaseAsync
                "identity profile distinct scope effects and erased acceptance remain verifiable"
                (run (withVerifiedLedger AdmitContract.lifecycle))
            testCaseAsync
                "normalized duplicates and role boundaries leave no partial actor"
                (run (withVerifiedLedger AdmitContract.boundary))
            testCaseAsync
                "maximum48 grants use50 ordered effects and one profile compartment"
                (run (withVerifiedLedger AdmitContract.maximum))
        ]
