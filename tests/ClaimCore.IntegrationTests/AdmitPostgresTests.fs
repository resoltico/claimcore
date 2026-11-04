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
        let! before = verified database

        do!
            sql
                database
                "UPDATE claimcore.actors SET display_name='Resurrected projection',contact='Wrong contact' WHERE actor_id=$1"
                [| box (id 740) |]

        do! finding VerificationFinding.AuthorityProfileProjection database
        do! enterMaintenance database
        let! rebuilt = ProjectionRebuild.rebuild database.OwnerSource before token

        Expect.isOk
            rebuilt
            "Verified admission effects rebuild without resurrecting an erased profile"

        let! after = verified database
        Expect.equal after before "Rebuild preserves the whole operation's final proof"

        let! person =
            database.Fixture.Store.Read
                {
                    CancellationToken = token
                    Work = (fun tx -> tx.FindActor(CaseFixtures.actorId (id 740)))
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
                    ActorId = id 740
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
        use! connection = database.OwnerSource.OpenConnectionAsync()
        use! transaction = connection.BeginTransactionAsync token
        let! head, instant = Transactions.head connection token
        let operation = CaseFixtures.operationId (id 930)
        let target = ActorId.ofOperationId operation

        let principal =
            PrincipalKey.human "https://issuer.example/" "duplicate-retained-admission"
            |> Result.defaultWith (string >> failtest)

        let profile =
            ActorProfile.parse Inputs.actorProfile
            |> Result.defaultWith (fun _ -> failtest "Fixture profile")

        let duplicate =
            AuthorityEffect.Grant
                {
                    Role = Role.CaseReader
                    Scope = RecordedGrantScope.Installation
                }

        let effects =
            [
                AuthorityEffect.Register(PrincipalKey.toInput principal)
                AuthorityEffect.SetProfile profile
                duplicate
                duplicate
            ]

        let mutable salt = 0uy

        let rows =
            LedgerEntries.createAuthorityOperation
                AuthorityKind.Admit
                (fun () ->
                    salt <- salt + 1uy
                    Array.create 32 salt)
                {
                    InstallationId = database.Fixture.InstallationId
                    Head = head
                    Instant = instant
                    ActorId = CaseFixtures.actorId (id 1)
                    OperationId = operation
                    Effects =
                        effects
                        |> List.map (fun effect ->
                            {
                                Subject =
                                    EntryTarget.Authority(target, AuthorityEffect.kind effect)
                                Request =
                                    AuthorityRecord.encode
                                        {
                                            OperationId = OperationId.value operation
                                            TargetActorId = target
                                            Effect = effect
                                        }
                                Snapshot = Array.empty
                            })
                }
            |> Result.defaultWith (fun error ->
                failtestf "Metadata-complete duplicate witness: %A" error)

        for row in rows do
            let! _ = Persistence.entry connection row token
            ()

        let! _ =
            Persistence.authority connection (AuthorityProjection.Register(target, principal)) token

        let! _ =
            Persistence.authority connection (AuthorityProjection.SetProfile(target, profile)) token

        let! _ =
            Persistence.authority
                connection
                (AuthorityProjection.AddGrant(target, Role.CaseReader, None))
                token

        do! transaction.CommitAsync token
        let! result = Verification.verify database.OwnerSource None token

        match result with
        | VerificationResult.Diverged {Finding=VerificationFinding.AuthorityFold;Sequence=Some sequence} ->
            Expect.equal
                sequence
                (List.last rows).Sequence
                "The second normalized grant is the authoritative divergence"
        | other ->
            failtestf
                "A complete salted group must still reject duplicate admission semantics: %A"
                other
    }

[<Tests>]
let tests =
    testList
        "atomic authority admission"
        [
            testCaseAsync
                "erased admission rebuild preserves all grants without reviving names"
                (run erasedRebuild)
            testCaseAsync
                "complete cryptographic duplicate-grant group is rejected by authoritative fold"
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
                "maximum48 grants use50 fresh effect nonces and a final proof"
                (run (withVerifiedLedger AdmitContract.maximum))
        ]
