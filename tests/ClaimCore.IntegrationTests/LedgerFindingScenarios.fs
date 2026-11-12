module ClaimCore.IntegrationTests.LedgerFindingScenarios

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport
open ClaimCore.IntegrationTests.VerificationScenarioInputs

let private corruption expected statement database =
    task {
        let! _ = populated database
        do! guardedSql database statement [||]
        do! finding expected database
    }

let internal link database =
    corruption
        VerificationFinding.Link
        "UPDATE claimcore.ledger SET previous_hash=set_byte(previous_hash,0,(get_byte(previous_hash,0)+1)%256) WHERE sequence=4"
        database

let internal operationEffectGroup database =
    corruption
        VerificationFinding.PayloadFormat
        "DELETE FROM claimcore.ledger_effects WHERE operation_sequence=1 AND effect_index=1"
        database

let internal entryHash database =
    corruption
        VerificationFinding.PayloadFormat
        "UPDATE claimcore.ledger SET entry_hash=set_byte(entry_hash,0,(get_byte(entry_hash,0)+1)%256) WHERE sequence=4"
        database

let internal commitment database =
    corruption
        VerificationFinding.PayloadFormat
        "UPDATE claimcore.ledger_compartments SET nonce=set_byte(nonce,0,(get_byte(nonce,0)+1)%256) WHERE operation_sequence=4"
        database

let internal format (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            rewrite database (CaseFixtures.sequence 4L) (fun value ->
                let content = value.Root.Structure.Compartments |> List.exactlyOne
                let nonce = Compartment.content content |> Option.get |> fst

                let changed =
                    Compartment.seal
                        database.Fixture.InstallationId
                        value.OperationId
                        content.Descriptor
                        nonce
                        [| 0x7Fuy |]
                    |> Result.defaultWith (string >> failtest)

                OperationRoot.append
                    value.Root.Header
                    value.Root.Structure.Command
                    value.Root.Structure.Effects
                    [ changed ]
                |> Result.defaultWith (string >> failtest))

        do! finding VerificationFinding.PayloadFormat database
    }

let private close (database: PostgresFixture.Database) actor =
    task {
        let! reply =
            Service.execute
                actor
                (Inputs.caseOperation (
                    request database.Fixture 11 "synthetic-a" 1L (CaseAct.Bare BareAct.Close)
                ))
                token

        return accepted reply
    }

let internal revision (database: PostgresFixture.Database) =
    task {
        let! actor = populated database
        let! receipt = close database actor

        do!
            rewrite database receipt.Position.Sequence (fun value ->
                let request, snapshot = livePayload value

                updateCase
                    { request with ExpectedRevision = 2L }
                    { snapshot with
                        Revision = CaseFixtures.revision 3L
                    }
                    value)

        do! finding VerificationFinding.RevisionSequence database
    }

let internal start (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let! reply =
            Service.execute
                actor
                (Inputs.caseOperation (
                    request database.Fixture 12 "other-reference" 0L (CaseAct.Open registration)
                ))
                token

        let second = accepted reply

        do! sql database "DROP INDEX claimcore.ledger_creation_reference" [||]

        do!
            rewrite database second.Position.Sequence (fun value ->
                let request, snapshot = livePayload value
                let reference = CaseFixtures.reference "synthetic-a"

                let snapshot =
                    { snapshot with
                        Reference = reference
                        State =
                            CaseState.map
                                (fun fields ->
                                    { fields with
                                        CaseReference = reference
                                    })
                                snapshot.State
                    }

                updateCase
                    { request with
                        CaseReference = "synthetic-a"
                    }
                    snapshot
                    value)

        do! finding VerificationFinding.HistoryStart database
    }

let internal stateSequence (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let! granted =
            grant
                actor
                (id database.Fixture 30)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority granted
        let! receipt = close database actor

        do!
            rewrite database receipt.Position.Sequence (fun value ->
                let request, snapshot = livePayload value

                updateCase
                    { request with
                        Act = CaseAct.Reinstate "Confirmed"
                    }
                    snapshot
                    value)

        do! finding VerificationFinding.StateSequence database
    }

let internal erasure (database: PostgresFixture.Database) =
    task {
        let! actor = populated database
        let! original = row database (CaseFixtures.sequence 4L)

        let nonce, bytes =
            original.Root.Structure.Compartments
            |> List.exactlyOne
            |> Compartment.content
            |> Option.get

        let! granted =
            grant
                actor
                (id database.Fixture 30)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority granted

        let! erased =
            Service.execute
                actor
                (Inputs.caseOperation (
                    request database.Fixture 31 "synthetic-a" 1L (CaseAct.Bare BareAct.Erase)
                ))
                token

        accepted erased |> ignore
        let! _ = verified database

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger_compartments SET payload=$1,nonce=$2 WHERE operation_sequence=4"
                [| box bytes; box nonce |]

        do! finding VerificationFinding.ErasureIncomplete database
    }

let internal missingPayload (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger_compartments SET payload=NULL,nonce=NULL WHERE operation_sequence=4"
                [||]

        do! finding VerificationFinding.PayloadWithoutErasure database
    }

let internal authority (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let! reply =
            grant
                actor
                (id database.Fixture 20)
                database.Fixture.Owner
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                true

        let receipt = accepted reply
        do! rewrite database receipt.Position.Sequence (updateAuthority AuthorityAct.Disable)
        do! finding VerificationFinding.AuthorityFold database
    }

let internal executor (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            rewrite
                database
                (CaseFixtures.sequence 4L)
                (executor (CaseFixtures.actorId (id database.Fixture 99)))

        do! finding VerificationFinding.ActorNotEnabled database
    }

let internal human (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let service =
            PrincipalKey.service "https://issuer.example/" "service"
            |> Result.defaultWith (string >> failtest)

        do! register database.Fixture actor 30 service

        let! grant =
            grant
                actor
                (id database.Fixture 31)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority grant

        let! voided =
            Service.execute
                actor
                (Inputs.caseOperation (
                    request database.Fixture 32 "synthetic-a" 1L (CaseAct.Void "Entered in error")
                ))
                token

        do!
            rewrite
                database
                (accepted voided).Position.Sequence
                (VerificationScenarioInputs.executor (CaseFixtures.actorId (id database.Fixture 30)))

        do! finding VerificationFinding.HumanRequired database
    }

let internal owner (database: PostgresFixture.Database) =
    task {
        let! actor = populated database
        let reader = target "reader"
        do! register database.Fixture actor 30 reader

        let! changed =
            Service.execute
                actor
                (OperationInput.Authority(
                    authorityRequest (id database.Fixture 31) reader AuthorityAct.Enable
                ))
                token

        do!
            rewrite
                database
                (accepted changed).Position.Sequence
                (VerificationScenarioInputs.executor (CaseFixtures.actorId (id database.Fixture 30)))

        do! finding VerificationFinding.AuthorityFold database
    }

let internal caseProjection (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            sql
                database
                "UPDATE claimcore.cases SET snapshot=set_byte(snapshot,0,0) WHERE case_reference='synthetic-a'"
                [||]

        do! finding VerificationFinding.CaseProjection database
    }

let internal authorityProjection (database: PostgresFixture.Database) =
    task {
        let! _ = populated database
        do! sql database "DELETE FROM claimcore.grants WHERE role='CASE_HANDLER'" [||]
        do! finding VerificationFinding.AuthorityProjection database
    }

let internal head (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            sql
                database
                "UPDATE claimcore.ledger_head SET entry_hash=set_byte(entry_hash,0,(get_byte(entry_hash,0)+1)%256)"
                [||]

        do! finding VerificationFinding.Head database
    }

let internal expected (database: PostgresFixture.Database) =
    task {
        let! _ = populated database
        let! position = verified database

        let! current = LedgerAdministration.head database.OwnerSource token

        let checkpoint =
            InstallationCheckpoint.create
                current.InstallationId
                { position with
                    EntryHash = EntryHash.zero
                }
                current.ExpiredBefore
            |> Option.get

        let! result = Verification.verify database.OwnerSource (Some checkpoint) token

        Expect.equal
            result
            (VerificationResult.Diverged
                {
                    Sequence = Some position.Sequence
                    Finding = VerificationFinding.ExpectedPosition
                })
            "External checkpoint differs"
    }

let private indexFinding expected statement database =
    task {
        let! _ = populated database
        do! sql database statement [||]
        do! finding expected database
    }

let private payableIndexFinding expected statement database =
    task {
        let! actor = populated database

        let! decided =
            Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request
                        database.Fixture
                        11
                        "synthetic-a"
                        1L
                        (AuthoredCaseAct.Decide Inputs.decision)
                ))
                token

        accepted decided |> ignore
        let! _ = verified database
        do! sql database statement [||]
        do! finding expected database
    }

let internal profileProjection (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            sql
                database
                "UPDATE claimcore.actors SET display_name='Wrong projection' WHERE actor_id=$1"
                [| box (id database.Fixture 1) |]

        do! finding VerificationFinding.AuthorityProfileProjection database
    }

let internal findingCases =
    [
        VerificationFinding.Link, link
        VerificationFinding.PayloadFormat, operationEffectGroup
        VerificationFinding.PayloadFormat, entryHash
        VerificationFinding.PayloadFormat, commitment
        VerificationFinding.PayloadFormat, format
        VerificationFinding.RevisionSequence, revision
        VerificationFinding.HistoryStart, start
        VerificationFinding.StateSequence, stateSequence
        VerificationFinding.ErasureIncomplete, erasure
        VerificationFinding.PayloadWithoutErasure, missingPayload
        VerificationFinding.AuthorityProfileProjection, profileProjection
        VerificationFinding.AuthorityFold, authority
        VerificationFinding.ActorNotEnabled, executor
        VerificationFinding.HumanRequired, human
        VerificationFinding.AuthorityFold, owner
        VerificationFinding.CaseProjection, caseProjection
        VerificationFinding.CaseStatusProjection,
        indexFinding
            VerificationFinding.CaseStatusProjection
            "UPDATE claimcore.cases SET status='CLOSED'"
        VerificationFinding.CaseProgressProjection,
        indexFinding
            VerificationFinding.CaseProgressProjection
            "UPDATE claimcore.cases SET progress='PAID'"
        VerificationFinding.CaseChangedAtProjection,
        indexFinding
            VerificationFinding.CaseChangedAtProjection
            "UPDATE claimcore.cases SET changed_at=changed_at+interval '1 second'"
        VerificationFinding.CaseSearchProjection,
        indexFinding
            VerificationFinding.CaseSearchProjection
            "UPDATE claimcore.cases SET search_text='tampered'"
        VerificationFinding.CaseWorkProjection,
        indexFinding
            VerificationFinding.CaseWorkProjection
            "UPDATE claimcore.cases SET follow_up=DATE '2027-12-31'"
        VerificationFinding.CaseNotifiedOnProjection,
        indexFinding
            VerificationFinding.CaseNotifiedOnProjection
            "UPDATE claimcore.cases SET notified_on=DATE '2026-08-04'"
        VerificationFinding.CaseClaimedAmountProjection,
        indexFinding
            VerificationFinding.CaseClaimedAmountProjection
            "UPDATE claimcore.cases SET claimed_amount=1"
        VerificationFinding.CaseClaimedCurrencyProjection,
        indexFinding
            VerificationFinding.CaseClaimedCurrencyProjection
            "UPDATE claimcore.cases SET claimed_currency='USD'"
        VerificationFinding.CasePayableAmountProjection,
        payableIndexFinding
            VerificationFinding.CasePayableAmountProjection
            "UPDATE claimcore.cases SET payable_amount=1"
        VerificationFinding.CasePayableCurrencyProjection,
        payableIndexFinding
            VerificationFinding.CasePayableCurrencyProjection
            "UPDATE claimcore.cases SET payable_currency='USD'"

        VerificationFinding.AuthorityProjection, authorityProjection
        VerificationFinding.Head, head
        VerificationFinding.ExpectedPosition, expected
    ]
