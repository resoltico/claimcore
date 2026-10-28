module ClaimCore.IntegrationTests.LedgerFindingScenarios

open ClaimCore.TestSupport

open ClaimCore.Protocol

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport
open ClaimCore.IntegrationTests.VerificationScenarioInputs


let internal link (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger SET previous_hash=set_byte(previous_hash,0,(get_byte(previous_hash,0)+1)%256) WHERE sequence=4"
                [||]

        do! finding VerificationFinding.Link database
    }

let internal entryHash (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger SET entry_hash=set_byte(entry_hash,0,(get_byte(entry_hash,0)+1)%256) WHERE sequence=4"
                [||]

        do! finding VerificationFinding.EntryHash database
    }

let internal commitment (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger SET payload=set_byte(payload,octet_length(payload)-1,(get_byte(payload,octet_length(payload)-1)+1)%256) WHERE sequence=4"
                [||]

        do! finding VerificationFinding.Commitment database
    }

let internal format (database: PostgresFixture.Database) =
    task {
        let! _ = populated database
        do! rewrite database (CaseFixtures.sequence (4L)) (payload [| 0x7Fuy |] Array.empty)
        do! finding VerificationFinding.PayloadFormat database
    }

let internal revision (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        let change value =
            let request, snapshot = livePayload value

            value
            |> caseSubject (fun subject ->
                { subject with
                    Revision = CaseFixtures.revision 2L
                })
            |> updateCase
                { request with ExpectedRevision = 1L }
                { snapshot with
                    Revision = CaseFixtures.revision 2L
                }

        do! rewrite database (CaseFixtures.sequence (4L)) change
        do! finding VerificationFinding.RevisionSequence database
    }

let internal start (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        let change value =
            let request, snapshot = livePayload value

            let view =
                CaseState.map
                    (fun (fields: CaseFields) ->
                        { fields with
                            Status = CaseStatus.Closed
                        })
                    snapshot.State

            value
            |> caseSubject (fun subject -> { subject with Act = CaseActKind.Close })
            |> updateCase
                { request with
                    Act = (AuthoredCaseAct.Bare BareAct.Close)
                }
                { snapshot with State = view }

        do! rewrite database (CaseFixtures.sequence (4L)) change
        do! finding VerificationFinding.HistoryStart database
    }

let internal stateSequence (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let! closed =
            (Service.execute
                actor
                (OperationInput.Case(
                    request 11 "synthetic-a" 1L (AuthoredCaseAct.Bare BareAct.Close)
                ))
                (token))

        let position = (accepted closed).Position.Sequence

        let change value =
            let request, snapshot = livePayload value

            value
            |> caseSubject (fun subject ->
                { subject with
                    Act = CaseActKind.Reinstate
                })
            |> updateCase
                { request with
                    Act = (AuthoredCaseAct.Reinstate "Confirmed")
                }
                snapshot

        do! rewrite database position change
        do! finding VerificationFinding.StateSequence database
    }

let internal erasure (database: PostgresFixture.Database) =
    task {
        let! actor = populated database
        let! original = row database (CaseFixtures.sequence (4L))

        let! granted =
            grant
                actor
                (id 3)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority granted

        let! erased =
            (Service.execute
                actor
                (OperationInput.Case(
                    actRequest 11 "synthetic-a" 1L ((AuthoredCaseAct.Bare BareAct.Erase))
                ))
                (token))

        accepted erased |> ignore
        let! _ = verified database

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger SET payload=$1,payload_nonce=$2 WHERE sequence=4"
                [|
                    DatabaseSession.parameter original.Entry.Payload
                    DatabaseSession.parameter original.Entry.PayloadNonce
                |]

        do! finding VerificationFinding.ErasureIncomplete database
    }

let internal missingPayload (database: PostgresFixture.Database) =
    task {
        let! _ = populated database
        // This is the deliberate trusted-owner guard exception; runtime has no direct UPDATE right.
        do!
            sql
                database
                "UPDATE claimcore.ledger SET payload=NULL,payload_nonce=NULL WHERE sequence=4"
                [||]

        do! finding VerificationFinding.PayloadWithoutErasure database
    }

let internal authority (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let! added =
            grant
                actor
                (id 20)
                database.Fixture.Owner
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                true

        let sequence = (accepted added).Position.Sequence

        let altered =
            authorityRequest (id 20) (target "absent") (AuthorityAct.Enable)
            |> Authority.parse
            |> Result.defaultWith (fun _ -> failtest "Synthetic authority input must admit")
            |> AuthorityRequests.toRecord

        do!
            rewrite
                database
                sequence
                (authoritySubject (fun subject ->
                    { subject with
                        Act = AuthorityKind.Enable
                    })
                 >> updateAuthority altered)

        do! finding VerificationFinding.AuthorityFold database
    }

let internal executor (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            rewrite database (CaseFixtures.sequence (4L)) (fun value ->
                { value with
                    Metadata =
                        { value.Metadata with
                            ActorId = (CaseFixtures.actorId (id 99))
                        }
                })

        do! finding VerificationFinding.ActorNotEnabled database
    }

let internal human (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let service =
            PrincipalKey.service "https://issuer.example/" "service"
            |> Result.defaultWith (string >> failtest)

        do! register actor 3 service

        let! added =
            grant
                actor
                (id 4)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority added

        let! voided =
            (Service.execute
                actor
                (OperationInput.Case(
                    actRequest 11 "synthetic-a" 1L ((AuthoredCaseAct.Void "Entered in error"))
                ))
                (token))

        do!
            rewrite database (accepted voided).Position.Sequence (fun value ->
                { value with
                    Metadata =
                        { value.Metadata with
                            ActorId = (CaseFixtures.actorId (id 3))
                        }
                })

        do! finding VerificationFinding.HumanRequired database
    }

let internal owner (database: PostgresFixture.Database) =
    task {
        let! actor = populated database
        let reader = target "reader"
        do! register actor 3 reader

        let! changed =
            (Service.execute
                actor
                (OperationInput.Authority(authorityRequest (id 4) reader (AuthorityAct.Enable)))
                (token))

        do!
            rewrite database (accepted changed).Position.Sequence (fun value ->
                { value with
                    Metadata =
                        { value.Metadata with
                            ActorId = (CaseFixtures.actorId (id 3))
                        }
                })

        do! finding VerificationFinding.OwnerRequired database
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

        let checkpoint: LedgerPosition =
            {
                Sequence = position.Sequence
                EntryHash = EntryHash.zero
            }

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

let internal findingCases =
    [
        VerificationFinding.Link, link
        VerificationFinding.EntryHash, entryHash
        VerificationFinding.Commitment, commitment
        VerificationFinding.PayloadFormat, format
        VerificationFinding.RevisionSequence, revision
        VerificationFinding.HistoryStart, start
        VerificationFinding.StateSequence, stateSequence
        VerificationFinding.ErasureIncomplete, erasure
        VerificationFinding.PayloadWithoutErasure, missingPayload
        VerificationFinding.AuthorityFold, authority
        VerificationFinding.ActorNotEnabled, executor
        VerificationFinding.HumanRequired, human
        VerificationFinding.OwnerRequired, owner
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
        VerificationFinding.AuthorityProjection, authorityProjection
        VerificationFinding.Head, head
        VerificationFinding.ExpectedPosition, expected
    ]
