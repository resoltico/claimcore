module ClaimCore.IntegrationTests.AdmitNonceGuardTests

open System
open Expecto
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private nonceMessage = "operation effects reuse a payload nonce"

let private writeErasedGroup (database: PostgresFixture.Database) reuse =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        use! transaction = connection.BeginTransactionAsync token
        let! head, instant = Transactions.head connection token
        let operation = CaseFixtures.operationId (id 920)
        let target = ActorId.ofOperationId operation
        let owner = CaseFixtures.actorId (id 1)

        let principal =
            PrincipalKey.human "https://issuer.example/" "nonce-control-target"
            |> Result.defaultWith (string >> failtest)

        let profile =
            ActorProfile.parse Inputs.actorProfile
            |> Result.defaultWith (fun _ -> failtest "Fixture profile")

        let effects =
            [
                AuthorityEffect.Register(PrincipalKey.toInput principal)
                AuthorityEffect.SetProfile profile
                AuthorityEffect.Grant
                    {
                        Role = Role.CaseReader
                        Scope = RecordedGrantScope.Installation
                    }
            ]

        let mutable next = 0uy

        let rows =
            LedgerEntries.createAuthorityOperation
                AuthorityKind.Admit
                (fun () ->
                    next <- next + 1uy
                    Array.create 32 next)
                {
                    InstallationId = database.Fixture.InstallationId
                    Head = head
                    Instant = instant
                    ActorId = owner
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
            |> Result.defaultWith (fun error -> failtestf "Valid group construction: %A" error)
        // Rebuild all cryptographic links after deliberate nonce reuse, so the negative control
        // cannot be explained by an unrelated invalid commitment or header hash.
        let mutable previous = head

        let rows =
            rows
            |> List.mapi (fun index row ->
                let nonce =
                    if reuse && index = 1 then
                        rows.Head.PayloadNonce
                    else
                        row.PayloadNonce

                let commitment = LedgerEntry.commitment nonce row.Payload

                let subject =
                    match row.Subject with
                    | EntryTarget.Authority(target, act) ->
                        RecordedTarget.Authority { TargetActorId = target; Act = act }
                    | _ -> failtest "Expected authority effect"

                let metadata =
                    {
                        LedgerMetadata.Effects = row.Effects
                        InstallationId = database.Fixture.InstallationId
                        Sequence = row.Sequence
                        PreviousHash = EntryHash.toBytes previous.EntryHash
                        OperationId = row.OperationId
                        ActorId = row.ActorId
                        RecordedAt = row.RecordedAt
                        Subject = subject
                        CreationReference = None
                        PayloadCommitment = commitment
                    }

                let hash = LedgerEntry.hash metadata

                let changed =
                    { row with
                        PreviousHash = metadata.PreviousHash
                        EntryHash = hash
                        PayloadCommitment = commitment
                        PayloadNonce = nonce
                    }

                previous <- LedgerEntries.position row.Sequence hash
                changed)

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

        let eraseOperation = CaseFixtures.operationId (id 921)

        let erase =
            LedgerEntries.create
                {
                    InstallationId = database.Fixture.InstallationId
                    Head = previous
                    Instant = instant
                    Nonce = Array.create 32 255uy
                    ActorId = owner
                    OperationId = eraseOperation
                    Subject = EntryTarget.Authority(target, AuthorityKind.EraseProfile)
                    Request =
                        AuthorityRecord.encode
                            {
                                OperationId = OperationId.value eraseOperation
                                TargetActorId = target
                                Effect = AuthorityEffect.EraseProfile
                            }
                    Snapshot = Array.empty
                }

        let! _ = Persistence.entry connection erase token

        let! _ =
            Persistence.authority
                connection
                (AuthorityProjection.EraseProfile(target, erase.Sequence))
                token

        let witness = rows.Head.PayloadNonce, rows[1].Payload, rows[1].PayloadCommitment

        try
            do! transaction.CommitAsync token
            return None, witness
        with :? PostgresException as error ->
            return Some(error.SqlState, error.MessageText), witness
    }

let private protectedGuard (database: PostgresFixture.Database) =
    task {
        let! _ = bootstrap database.Fixture
        let! before = verified database
        let! rejected, _ = writeErasedGroup database true

        Expect.equal
            rejected
            (Some("CC004", nonceMessage))
            "The original INSERT nonce is checked even after later same-transaction erasure"

        let! after = verified database
        Expect.equal after before "Rejected erasure/reuse leaves no accepted prefix or projection"
        let! accepted, _ = writeErasedGroup database false
        Expect.isNone accepted "Independent nonces may be erased in their admission transaction"
        let! _ = verified database
        return ()
    }

let private counterfactual (database: PostgresFixture.Database) =
    task {
        let! _ = bootstrap database.Fixture
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! functionRows =
            DatabaseSession.rows
                connection
                "SELECT pg_get_functiondef('claimcore.complete_ledger_operation()'::regprocedure)"
                [||]
                token

        let original = DatabaseSession.value<string> 0 functionRows.Head

        let start =
            original.IndexOf("    -- Deferred INSERT events", StringComparison.Ordinal)

        let ending =
            original.IndexOf("    SELECT * INTO root", start, StringComparison.Ordinal)

        Expect.isTrue
            (start >= 0 && ending > start)
            "The control removes only the new original-nonce check"

        let weaker = original.Remove(start, ending - start)
        let! _ = DatabaseSession.execute connection weaker [||] token

        try
            let! accepted, (rootNonce, profilePayload, profileCommitment) =
                writeErasedGroup database true

            Expect.isNone
                accepted
                "Without the saved INSERT nonce check, later erasure hides reuse from the live-nonce count"

            let! _ = verified database

            Expect.equal
                (LedgerEntry.commitment rootNonce profilePayload)
                profileCommitment
                "The surviving root nonce now guesses an erased private payload despite a verifiable chain"
        finally
            DatabaseSession.execute connection original [||] token
            |> _.GetAwaiter().GetResult()
            |> ignore
    }

[<Tests>]
let tests =
    testList
        "admission erasure nonce controls"
        [
            testCaseAsync
                "saved INSERT salt rejects reuse while an independently salted erasure verifies"
                (run protectedGuard)
            testCaseAsync
                "removing only saved-salt check exposes a verifiable erased-profile guessing witness"
                (run counterfactual)
        ]
