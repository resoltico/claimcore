module ClaimCore.IntegrationTests.AdmitPostgresTests

open Expecto
open System
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Application
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
                    Work =
                        (fun tx ->
                            PeopleReads.findActor
                                tx
                                (CaseFixtures.actorId (id database.Fixture 740)))
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
            (task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (database.Fixture.Store)
                        ((id database.Fixture 930))
                        (principal)
                        (None)
                        "people.admit"
                        (ClaimCore.TestSupport.Submissions.profile (Inputs.actorProfile)
                         |> Map.add
                             (TaskInputId.Target InputTarget.Grants)
                             (ClaimCore.Application.Contracts.SubmissionValue.Rows(
                                 ([
                                     {
                                         Role = Role.CaseReader
                                         Scope = GrantTarget.Installation
                                     }
                                     {
                                         Role = Role.CaseHandler
                                         Scope = GrantTarget.Installation
                                     }
                                 ])
                                 |> List.map ClaimCore.TestSupport.Submissions.grant
                             )))

                return! Service.executeSubmission (owner) submission (token)
            })

        let receipt = accepted admitted
        let! _ = verified database

        do!
            rewrite database receipt.Position.Sequence (fun original ->
                let change = original.Changes |> List.exactlyOne

                let person =
                    match change.Value with
                    | PartValue.Person value -> value
                    | _ -> failtest "Person part required"

                let assignment = person.Grants.Head

                let forged =
                    { change with
                        Value =
                            PartValue.Person
                                { person with
                                    Grants = [ assignment; assignment ]
                                }
                    }

                let nonce =
                    original.Root.Structure.Compartments
                    |> List.exactlyOne
                    |> Compartment.content
                    |> Option.get
                    |> fst

                recreate
                    original.Root.Header
                    original.Root.Structure.Task
                    original.Root.Structure.Binding
                    original.Inputs.Value
                    [ forged ]
                    nonce)

        do! finding VerificationFinding.StateSequence database

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
                "valid-hash duplicate assignments do not reproduce the accepted admission"
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
