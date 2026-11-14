module ClaimCore.IntegrationTests.ActorProfilesPostgresTests

open System
open Expecto
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private refused (database: PostgresFixture.Database) state statement parameters =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let mutable actual = None

        try
            let! _ = DatabaseSession.execute connection statement parameters token
            ()
        with :? PostgresException as error ->
            actual <- Some error.SqlState

        Expect.equal actual (Some state) "The intended SQL guard refuses the mutation"
    }

let private lifecycle (database: PostgresFixture.Database) =
    task {
        do! ActorProfilesContract.lifecycle database.Fixture
        let! _ = verified database

        do!
            refused
                database
                "CC003"
                "UPDATE claimcore.ledger_compartments SET payload=NULL,nonce=NULL WHERE owner_kind='ACTOR_PROFILE' AND operation_sequence=(SELECT sequence FROM claimcore.ledger WHERE operation_id=$1)"
                [| box (id database.Fixture 304) |]

        let! _ = verified database

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger_compartments SET payload=NULL,nonce=NULL WHERE owner_kind='ACTOR_PROFILE' AND operation_sequence=(SELECT sequence FROM claimcore.ledger WHERE operation_id=$1)"
                [| box (id database.Fixture 304) |]

        do! finding VerificationFinding.PayloadWithoutErasure database
    }

let private noErase (database: PostgresFixture.Database) =
    task {
        let! _ = bootstrap database.Fixture

        do!
            refused
                database
                "CC002"
                "SELECT claimcore.erase_profile_payload($1)"
                [| box (id database.Fixture 1) |]

        do!
            refused
                database
                "CC003"
                "UPDATE claimcore.ledger_compartments SET payload=NULL,nonce=NULL WHERE owner_kind='ACTOR_PROFILE'"
                [||]

        let! _ = verified database
        return ()
    }

let private projection (database: PostgresFixture.Database) =
    task {
        let! owner = bootstrap database.Fixture
        let! before = checkpoint database

        do!
            sql
                database
                "UPDATE claimcore.actors SET display_name='Wrong projection' WHERE actor_id=$1"
                [| box (id database.Fixture 1) |]

        do! finding VerificationFinding.PeopleProfileProjection database
        do! enterMaintenance database
        let! result = ProjectionRebuild.rebuild database.OwnerSource before token
        Expect.isOk result "Verified profile history rebuilds the projection"
        let! after = verified database
        Expect.equal after before.Position "Rebuild changes no retained proof"
        let! context = Service.context owner () token

        match context with
        | Outcome.Succeeded {
                                Requester = RequesterState.Active context
                            } ->
            Expect.equal
                context.Self.DisplayName
                (DisplayName.Named(
                    PartyName.parse Inputs.ownerProfile.DisplayName
                    |> Result.defaultWith (string >> failtest)
                ))
                "Rebuild restores authored profile instead of identity-provider names"
        | other -> failtestf "Owner context after rebuild: %A" other
    }

let private rollback (database: PostgresFixture.Database) =
    task {
        do! ActorProfilesContract.atomicWrites database.Fixture
        let! _ = verified database
        return ()
    }

[<Tests>]
let tests =
    testList
        "people profile SQL and fold boundaries"
        [
            testCaseAsync
                "erasure frontier preserves later profile and rejects unauthorized holes"
                (run lifecycle)
            testCaseAsync
                "guard and privileged erasure function require an accepted erase"
                (run noErase)
            testCaseAsync
                "profile projection finding and rebuild preserve proof and authored name"
                (run projection)
            testCaseAsync
                "registration and erasure rollback retain a fully verifiable history"
                (run rollback)
        ]
