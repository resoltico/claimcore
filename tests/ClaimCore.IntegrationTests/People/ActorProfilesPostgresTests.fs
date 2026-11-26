module ClaimCore.IntegrationTests.ActorProfilesPostgresTests

open System
open Expecto
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.Application
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
                "UPDATE claimcore.ledger_compartments SET payload=NULL,nonce=NULL,nonce_fingerprint=NULL WHERE owner_kind='PROFILE' AND operation_sequence=(SELECT sequence FROM claimcore.ledger WHERE operation_id=$1)"
                [| box (id database.Fixture 304) |]

        let! _ = verified database

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger_compartments SET payload=NULL,nonce=NULL,nonce_fingerprint=NULL WHERE owner_kind='PROFILE' AND operation_sequence=(SELECT sequence FROM claimcore.ledger WHERE operation_id=$1)"
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
                "SELECT claimcore.clear_owner('PROFILE',$1)"
                [| box (id database.Fixture 1) |]

        do!
            refused
                database
                "CC003"
                "UPDATE claimcore.ledger_compartments SET payload=NULL,nonce=NULL,nonce_fingerprint=NULL WHERE owner_kind='PROFILE'"
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

        let! result =
            ProjectionRebuild.rebuild
                database.OwnerSource
                database.MasterKey
                before
                (MaintenanceHandle.create
                    1L
                    (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
                 |> Option.get)
                token

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
                    PartyName.parse DomainInputs.ownerProfile.DisplayName
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

let private actorSnapshot (source: NpgsqlDataSource) actorId =
    task {
        use! connection = source.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT row_to_json(a)::text FROM claimcore.actors a WHERE actor_id=$1"
                [| box actorId |]
                token

        return rows |> List.map (DatabaseSession.value<string> 0)
    }

let private clearingRefused (database: PostgresFixture.Database) actorId =
    task {
        let! before = actorSnapshot database.OwnerSource actorId
        Expect.hasLength before 1 "The negative control targets an existing actor"

        for source in [ database.OwnerSource; database.RuntimeSource ] do
            use! connection = source.OpenConnectionAsync()
            use! transaction = connection.BeginTransactionAsync()

            let! changed =
                DatabaseSession.execute
                    connection
                    "UPDATE claimcore.actors SET display_name=NULL,contact=NULL,display_name_search=NULL,contact_search=NULL WHERE actor_id=$1"
                    [| box actorId |]
                    token

            Expect.equal changed 1 "The real row was tentatively cleared before the deferred gate"
            let mutable state = None

            try
                do! transaction.CommitAsync()
            with :? PostgresException as error ->
                state <- Some error.SqlState

            Expect.equal
                state
                (Some "CC003")
                "Commit refuses structurally unsupported private clearing"

            let! after = actorSnapshot database.OwnerSource actorId
            Expect.equal after before "The entire original actor row remains after refused commit"
    }

let private currentProfileGuard (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = bootstrap fixture
        let actorId = id fixture 1
        do! clearingRefused database actorId

        let executeFor principal number taskName values =
            task {
                let! input =
                    personSubmission
                        fixture.Store
                        (id fixture number)
                        principal
                        None
                        taskName
                        values

                let! reply = Service.executeSubmission owner input token
                accepted reply |> ignore
            }

        let execute = executeFor fixture.Owner
        // Clearing contact is ordinary profile replacement, not a private-data erasure.
        do!
            execute
                351
                "people.setProfile"
                (Submissions.profile
                    {
                        DisplayName = "Contact cleared"
                        DigestEmail = None
                        Contact = None
                    })

        do!
            execute
                352
                "people.grant"
                (Submissions.grant
                    {
                        Role = Role.Auditor
                        Scope = GrantTarget.Installation
                    })

        do! clearingRefused database actorId
        do! execute 353 "people.eraseProfile" Map.empty
        let! erased = actorSnapshot database.OwnerSource actorId
        Expect.hasLength erased 1 "A real accepted erase retains the structural actor identity"
        // A grant after erasure carries the absent profile without requiring another erasure.
        do!
            execute
                354
                "people.revoke"
                (Submissions.grant
                    {
                        Role = Role.Auditor
                        Scope = GrantTarget.Installation
                    })

        let! _ = verified database

        do!
            execute
                355
                "people.setProfile"
                (Submissions.profile
                    {
                        DisplayName = "Replacement after erasure"
                        DigestEmail = None
                        Contact = Some "synthetic-contact"
                    })
        // An old retained erasure cannot authorize clearing a later private revision.
        do! clearingRefused database actorId
        let! _ = verified database
        do! execute 356 "people.eraseProfile" Map.empty

        let other =
            PrincipalKey.human "https://issuer.example/" "profile-guard-other"
            |> Result.defaultWith (string >> failtest)

        let otherExecute = executeFor other

        do!
            otherExecute
                357
                "people.register"
                (Submissions.profile
                    {
                        DisplayName = "Synthetic profile"
                        DigestEmail = None
                        Contact = None
                    })

        do! otherExecute 358 "people.disable" Map.empty
        do! otherExecute 359 "people.eraseProfile" Map.empty
        do! otherExecute 360 "people.enable" Map.empty

        do!
            otherExecute
                361
                "people.grant"
                (Submissions.grant
                    {
                        Role = Role.Auditor
                        Scope = GrantTarget.Installation
                    })

        let! _ = verified database
        let! before = checkpoint database
        do! enterMaintenance database

        let! rebuilt =
            ProjectionRebuild.rebuild
                database.OwnerSource
                database.MasterKey
                before
                (MaintenanceHandle.create 1L (Guid.Parse "10000000-0000-4000-8000-000000000099")
                 |> Option.get)
                token

        Expect.isOk rebuilt "Deletion and reconstruction preserve the current erased shape"
        let! after = verified database
        Expect.equal after before.Position "Reconstruction keeps accepted evidence intact"
    }

[<Tests>]
let tests =
    testList
        "people profile SQL and fold boundaries"
        [
            testCaseAsync
                "latest immutable person compartment guards direct profile clearing and stale erasures"
                (run currentProfileGuard)
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
