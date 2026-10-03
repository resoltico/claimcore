module ClaimCore.IntegrationTests.ActorGrantStoreTests

open System
open System.Data
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.FreshBaselineSupport
open ClaimCore.IntegrationTests.ActorGrantTestSupport

let private initialOwner =
    testCase "[CC-AUTH-001] initial owner is private and unknown login remains ungranted" (fun _ ->
        Expect.isError
            (PrincipalKey.human ("https://issuer.example.test/" + String('a', 2048)) "person")
            "Application issuer length must match the fresh database bound."

        withAuthorityDatabase (fun owner app witness ->
            use source = RuntimeDataSource.create app
            let store = new ActorGrantStore(source)
            let ownerPrincipal = human "first-owner"
            let unknown = human "unknown-login"

            Expect.isNone
                (load store unknown ResourceScope.Installation)
                "A valid identity token cannot promote its subject."

            provision owner witness ownerPrincipal |> applied

            let authority =
                load store ownerPrincipal ResourceScope.Installation
                |> Option.defaultWith (fun () -> failtest "Initial owner must be stored.")

            Expect.isTrue
                (ActorAuthorization.can
                    ownerPrincipal
                    authority
                    EndpointAction.ManageGrants
                    ResourceScope.Installation)
                "Only explicit owner grant authorizes administration."

            Expect.isNone
                (load store unknown ResourceScope.Installation)
                "Unknown remains denied."

            Expect.equal
                (provision owner witness (human "second-first-owner"))
                AuthorityWriteOutcome.Refused
                "Private initial-owner action cannot run twice."))

let private revocation =
    testCase
        "[CC-AUTH-001] explicit grant revocation advances authority and invalidates stale snapshot"
        (fun _ ->
            withAuthorityDatabase (fun owner app witness ->
                let ownerPrincipal = human "owner"
                let readerPrincipal = human "reader"
                provision owner witness ownerPrincipal |> applied
                use source = RuntimeDataSource.create app
                let grants = new ActorGrantStore(source)
                let registry = new ActorGrantRegistry(source, witness)
                registry.RegisterActor(ownerPrincipal, readerPrincipal) |> await |> applied
                let readerId = actorId grants readerPrincipal

                let grant =
                    {
                        Role = Role.CaseReader
                        Scope = GrantScope.Installation
                    }

                registry.SetGrant(ownerPrincipal, readerId, grant, true) |> await |> applied

                let before =
                    load grants readerPrincipal ResourceScope.Installation
                    |> Option.defaultWith (fun () -> failtest "Reader is missing.")

                Expect.isTrue
                    (ActorAuthorization.can
                        readerPrincipal
                        before
                        EndpointAction.ListCases
                        ResourceScope.Installation)
                    "An explicit grant allows the scoped action."

                registry.SetGrant(ownerPrincipal, readerId, grant, false) |> await |> applied

                let after =
                    load grants readerPrincipal ResourceScope.Installation
                    |> Option.defaultWith (fun () ->
                        failtest "Reader should be disabled, not deleted.")

                assertRevoked readerPrincipal before after))

let private assertInitialReadback owner (witness: WitnessProtocol) principal expected =
    let tip = witness.Snapshot().TipSequence
    Expect.equal (provision owner witness principal) expected "Exact provisioning receipt."
    Expect.equal (witness.Snapshot().TipSequence) tip "Readback adds no authority event."

let private initialOwnerReadback =
    testCase
        "[CC-AUTH-001] exact initial-owner readback preserves receipt without restoring authority"
        (fun _ ->
            withAuthorityDatabase (fun owner app witness ->
                let first = human "initial-owner-readback"
                let second = human "replacement-owner"
                let original = provision owner witness first
                original |> applied
                assertInitialReadback owner witness first original

                use source = RuntimeDataSource.create app
                let grants = new ActorGrantStore(source)
                let registry = new ActorGrantRegistry(source, witness)
                registry.RegisterActor(first, second) |> await |> applied

                let ownerGrant =
                    {
                        Role = Role.Owner
                        Scope = GrantScope.Installation
                    }

                registry.SetGrant(first, actorId grants second, ownerGrant, true)
                |> await
                |> applied

                registry.SetEnabled(first, actorId grants first, false) |> await |> applied
                assertInitialReadback owner witness first original

                let current = load grants first ResourceScope.Installation |> Option.get
                Expect.isFalse current.Enabled "Readback cannot revive the old owner."

                Expect.equal
                    (provision owner witness (human "unrelated-bootstrap"))
                    AuthorityWriteOutcome.Refused
                    "A different principal cannot replace the initial owner."))

let private stalePrimary =
    testCase
        "[CC-AUTH-001] stale primary cannot repeat initial owner against surviving witness"
        (fun _ ->
            withAuthorityDatabase (fun owner _ witness ->
                provision owner witness (human "owner-before-restore") |> applied
                execute owner "DELETE FROM claimcore.actor_grants"
                execute owner "DELETE FROM claimcore.actor_authority_events"
                execute owner "DELETE FROM claimcore.actors"
                execute owner "UPDATE claimcore.authority_tip SET revision=0 WHERE singleton"

                Expect.equal
                    (provision owner witness (human "owner-after-restore"))
                    AuthorityWriteOutcome.Refused
                    "A surviving independent witness fences a rolled-back authority tip."))

let private dualControlRoster =
    testCase
        "[CC-AUTH-001] real-data roster needs a second distinct human owner or steward"
        (fun _ ->
            withAuthorityDatabase (fun owner app witness ->
                let first = human "owner-one"
                let second = human "steward-two"
                provision owner witness first |> applied
                use source = RuntimeDataSource.create app

                Expect.equal
                    (ActorGrantDeployment.verifyRoster source |> await)
                    DualControlRoster.Missing
                    "A single initial owner is not a real-data or dual-control roster."

                let registry = new ActorGrantRegistry(source, witness)
                registry.RegisterActor(first, second) |> await |> applied

                Expect.equal
                    (ActorGrantDeployment.verifyRoster source |> await)
                    DualControlRoster.Missing
                    "A second login without ClaimCore grant is not a steward."

                let reader = new ActorGrantStore(source)
                let secondId = actorId reader second

                let grant =
                    {
                        Role = Role.DataSteward
                        Scope = GrantScope.Installation
                    }

                registry.SetGrant(first, secondId, grant, true) |> await |> applied

                match ActorGrantDeployment.verifyRoster source |> await with
                | DualControlRoster.Ready revision ->
                    Expect.isGreaterThan
                        revision
                        1L
                        "Distinct active steward grant advances authority."
                | DualControlRoster.Missing ->
                    failtest "Distinct steward should satisfy only the roster prerequisite."))

let private replayProjection owner =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT revision,event_id,action_name,target_actor_id,approver_actor_id,canonical_action "
            + "FROM claimcore.actor_authority_events ORDER BY revision",
            connection
        )

    use rows = command.ExecuteReader()
    let mutable projection = AuthorityHistory.empty

    while rows.Read() do
        let approver = if rows.IsDBNull(4) then None else Some(rows.GetGuid(4))

        let action =
            ActorGrantCandidate.decodeStored
                (rows.GetFieldValue<byte array>(5))
                (rows.GetInt64(0))
                (rows.GetGuid(1))
                (rows.GetString(2))
                (rows.GetGuid(3))
                approver
            |> Option.defaultWith (fun () -> failtest "Canonical authority row is invalid.")

        projection <-
            AuthorityHistory.apply projection action
            |> Result.defaultWith (fun _ -> failtest "Authority sequence must replay.")

    projection

let private projectionReplay =
    testCase "[CC-AUTH-001] exact authority replay detects a forged live actor projection" (fun _ ->
        withAuthorityDatabase (fun owner app witness ->
            let first = human "projection-owner"
            let second = human "projection-reader"
            provision owner witness first |> applied
            use source = RuntimeDataSource.create app
            let registry = new ActorGrantRegistry(source, witness)
            registry.RegisterActor(first, second) |> await |> applied
            let expected = replayProjection owner
            let readerId = actorId (new ActorGrantStore(source)) second
            Expect.isTrue expected.Actors[readerId].Enabled "Witnessed actor is enabled."

            assertProjectionTamper owner source witness readerId expected.Actors[readerId].Enabled))

let private serviceCannotOwn =
    testCase
        "[CC-AUTH-001] service principals cannot acquire human owner or steward grants"
        (fun _ ->
            withAuthorityDatabase (fun owner app witness ->
                let ownerPrincipal = human "human-owner"
                let automation = service "batch-client"

                Expect.equal
                    (provision owner witness automation)
                    AuthorityWriteOutcome.Refused
                    "Initial owner must be human."

                provision owner witness ownerPrincipal |> applied
                use source = RuntimeDataSource.create app
                let registry = new ActorGrantRegistry(source, witness)
                registry.RegisterActor(ownerPrincipal, automation) |> await |> applied
                let targetId = actorId (new ActorGrantStore(source)) automation

                for role in [ Role.Owner; Role.DataSteward ] do
                    let grant =
                        {
                            Role = role
                            Scope = GrantScope.Installation
                        }

                    Expect.equal
                        (registry.SetGrant(ownerPrincipal, targetId, grant, true) |> await)
                        AuthorityWriteOutcome.Refused
                        "Service client cannot acquire human governance authority."))

let tests =
    testList
        "actor and grant storage"
        [
            initialOwner
            initialOwnerReadback
            revocation
            stalePrimary
            dualControlRoster
            projectionReplay
            serviceCannotOwn
        ]
