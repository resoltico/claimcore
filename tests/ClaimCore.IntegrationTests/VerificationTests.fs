module ClaimCore.IntegrationTests.VerificationTests

open ClaimCore.Contract

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreContractTests
open ClaimCore.IntegrationTests.VerificationSupport

let private livePayload (value: LedgerRow) =
    let bytes =
        value.Entry.Payload
        |> Option.defaultWith (fun () -> failtest "Retained case fixture is required")

    let frame =
        LedgerEntry.decodePayload 262144 bytes
        |> Result.defaultWith (fun _ -> failtest "Invalid fixture frame")

    let request =
        RequestRecord.decode 65536 frame.Request
        |> Result.defaultWith (fun _ -> failtest "Invalid fixture request")

    let snapshot =
        ClaimCore.RecordFormat.CaseRecord.decodeSnapshot frame.Snapshot
        |> Result.defaultWith (fun _ -> failtest "Invalid fixture snapshot")

    request, snapshot

let private updateCase request snapshot value =
    payload
        (RequestRecord.encode request)
        (ClaimCore.RecordFormat.CaseRecord.encodeSnapshot snapshot)
        value

let private updateAuthority request value =
    payload (AuthorityRecord.encode request) Array.empty value

let private target subject =
    PrincipalKey.human "https://issuer.example/" subject
    |> Result.defaultWith failtest

let private register (actor: IActorClaimsCore) n principal =
    task {
        let! outcome =
            actor.ExecuteAuthority(
                authorityRequest (id n) principal AuthorityAct.RegisterActor,
                token
            )

        acceptedAuthority outcome
    }

let private link (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger SET previous_hash=set_byte(previous_hash,0,(get_byte(previous_hash,0)+1)%256) WHERE sequence=4"
                [||]

        do! finding VerificationFinding.Link database
    }

let private entryHash (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger SET entry_hash=set_byte(entry_hash,0,(get_byte(entry_hash,0)+1)%256) WHERE sequence=4"
                [||]

        do! finding VerificationFinding.EntryHash database
    }

let private commitment (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger SET payload=set_byte(payload,octet_length(payload)-1,(get_byte(payload,octet_length(payload)-1)+1)%256) WHERE sequence=4"
                [||]

        do! finding VerificationFinding.Commitment database
    }

let private format (database: PostgresFixture.Database) =
    task {
        let! _ = populated database
        do! rewrite database 4L (payload [| 0x7Fuy |] Array.empty)
        do! finding VerificationFinding.PayloadFormat database
    }

let private revision (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        let change value =
            let request, snapshot = livePayload value

            value
            |> caseScope (fun scope -> { scope with Revision = 2L })
            |> updateCase { request with ExpectedRevision = 1L } { snapshot with Revision = 2L }

        do! rewrite database 4L change
        do! finding VerificationFinding.RevisionSequence database
    }

let private start (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        let change value =
            let request, snapshot = livePayload value

            let view =
                match snapshot.View with
                | CaseView.Live(fields, disposition) ->
                    CaseView.Live(
                        { fields with
                            Status = CaseStatus.Closed
                        },
                        disposition
                    )
                | _ -> failtest "Live fixture required"

            value
            |> caseScope (fun scope ->
                { scope with
                    Act = (CaseActKind.Business BusinessActKind.Close)
                })
            |> updateCase
                { request with
                    Act = CaseAct.Business BusinessCommand.Close
                }
                { snapshot with View = view }

        do! rewrite database 4L change
        do! finding VerificationFinding.HistoryStart database
    }

let private disposition (database: PostgresFixture.Database) =
    task {
        let! actor = populated database
        let! closed = actor.Execute(request 11 "synthetic-a" 1L BusinessCommand.Close, token)
        let position = (accepted closed).Position.Sequence

        let change value =
            let request, snapshot = livePayload value

            value
            |> caseScope (fun scope ->
                { scope with
                    Act = (CaseActKind.Stewardship StewardshipActKind.Reinstate)
                })
            |> updateCase
                { request with
                    Act = CaseAct.Stewardship(StewardshipAct.Reinstate "Confirmed")
                }
                snapshot

        do! rewrite database position change
        do! finding VerificationFinding.DispositionSequence database
    }

let private erasure (database: PostgresFixture.Database) =
    task {
        let! actor = populated database
        let! original = row database 4L

        let! granted =
            grant actor (id 3) database.Fixture.Owner Role.DataSteward GrantTarget.Installation true

        acceptedAuthority granted

        let! erased =
            actor.Execute(
                actRequest 11 "synthetic-a" 1L (CaseAct.Stewardship StewardshipAct.Erase),
                token
            )

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

let private missingPayload (database: PostgresFixture.Database) =
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

let private authority (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let! added =
            grant actor (id 20) database.Fixture.Owner Role.CaseReader GrantTarget.Installation true

        let sequence = (accepted added).Position.Sequence

        let altered =
            authorityRequest (id 20) (target "absent") (AuthorityAct.SetEnabled true)
            |> AuthorityRequests.toRecord

        do!
            rewrite
                database
                sequence
                (authorityScope (fun scope ->
                    { scope with
                        Act = RecordedAuthorityKind.SetEnabled
                    })
                 >> updateAuthority altered)

        do! finding VerificationFinding.AuthorityFold database
    }

let private executor (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            rewrite database 4L (fun value ->
                { value with
                    Metadata = { value.Metadata with ActorId = id 99 }
                })

        do! finding VerificationFinding.ActorNotEnabled database
    }

let private human (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let service =
            PrincipalKey.service "https://issuer.example/" "service"
            |> Result.defaultWith failtest

        do! register actor 3 service

        let! added =
            grant actor (id 4) database.Fixture.Owner Role.DataSteward GrantTarget.Installation true

        acceptedAuthority added

        let! voided =
            actor.Execute(
                actRequest
                    11
                    "synthetic-a"
                    1L
                    (CaseAct.Stewardship(StewardshipAct.Void "Entered in error")),
                token
            )

        do!
            rewrite database (accepted voided).Position.Sequence (fun value ->
                { value with
                    Metadata = { value.Metadata with ActorId = id 3 }
                })

        do! finding VerificationFinding.HumanRequired database
    }

let private owner (database: PostgresFixture.Database) =
    task {
        let! actor = populated database
        let reader = target "reader"
        do! register actor 3 reader

        let! changed =
            actor.ExecuteAuthority(
                authorityRequest (id 4) reader (AuthorityAct.SetEnabled true),
                token
            )

        do!
            rewrite database (accepted changed).Position.Sequence (fun value ->
                { value with
                    Metadata = { value.Metadata with ActorId = id 3 }
                })

        do! finding VerificationFinding.OwnerRequired database
    }

let private caseProjection (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            sql
                database
                "UPDATE claimcore.cases SET claimant_name='Tampered synthetic' WHERE case_reference='synthetic-a'"
                [||]

        do! finding VerificationFinding.CaseProjection database
    }

let private authorityProjection (database: PostgresFixture.Database) =
    task {
        let! _ = populated database
        do! sql database "DELETE FROM claimcore.grants WHERE role='CASE_HANDLER'" [||]
        do! finding VerificationFinding.AuthorityProjection database
    }

let private head (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            sql
                database
                "UPDATE claimcore.ledger_head SET entry_hash=set_byte(entry_hash,0,(get_byte(entry_hash,0)+1)%256)"
                [||]

        do! finding VerificationFinding.Head database
    }

let private expected (database: PostgresFixture.Database) =
    task {
        let! _ = populated database
        let! position = verified database

        let checkpoint: ExpectedPosition =
            {
                Sequence = position.Sequence
                EntryHash = Array.zeroCreate 32
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

let private findingCases =
    [
        VerificationFinding.Link, link
        VerificationFinding.EntryHash, entryHash
        VerificationFinding.Commitment, commitment
        VerificationFinding.PayloadFormat, format
        VerificationFinding.RevisionSequence, revision
        VerificationFinding.HistoryStart, start
        VerificationFinding.DispositionSequence, disposition
        VerificationFinding.ErasureIncomplete, erasure
        VerificationFinding.PayloadWithoutErasure, missingPayload
        VerificationFinding.AuthorityFold, authority
        VerificationFinding.ActorNotEnabled, executor
        VerificationFinding.HumanRequired, human
        VerificationFinding.OwnerRequired, owner
        VerificationFinding.CaseProjection, caseProjection
        VerificationFinding.AuthorityProjection, authorityProjection
        VerificationFinding.Head, head
        VerificationFinding.ExpectedPosition, expected
    ]

let private disabledExecutor (database: PostgresFixture.Database) =
    task {
        let! actor = populated database
        let reader = target "disabled"
        do! register actor 3 reader

        let! disabled =
            actor.ExecuteAuthority(
                authorityRequest (id 4) reader (AuthorityAct.SetEnabled false),
                token
            )

        acceptedAuthority disabled

        let! opened =
            actor.Execute(request 11 "another" 0L (BusinessCommand.Open registration), token)

        do!
            rewrite database (accepted opened).Position.Sequence (fun value ->
                { value with
                    Metadata = { value.Metadata with ActorId = id 3 }
                })

        do! finding VerificationFinding.ActorNotEnabled database
    }

let private serviceGrantTarget (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let service =
            PrincipalKey.service "https://issuer.example/" "service"
            |> Result.defaultWith failtest

        do! register actor 3 service

        let! granted =
            grant actor (id 4) service Role.CaseReader GrantTarget.Installation true

        let altered =
            authorityRequest
                (id 4)
                service
                (AuthorityAct.SetGrant(Role.Owner, GrantTarget.Installation, true))
            |> AuthorityRequests.toRecord

        do! rewrite database (accepted granted).Position.Sequence (updateAuthority altered)
        do! finding VerificationFinding.HumanRequired database
    }

let private lastOwner (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let! unchanged =
            actor.ExecuteAuthority(
                authorityRequest (id 20) database.Fixture.Owner (AuthorityAct.SetEnabled true),
                token
            )

        let altered =
            authorityRequest (id 20) database.Fixture.Owner (AuthorityAct.SetEnabled false)
            |> AuthorityRequests.toRecord

        do! rewrite database (accepted unchanged).Position.Sequence (updateAuthority altered)
        do! finding VerificationFinding.AuthorityFold database
    }

let private permanentPolicyOnly (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        let reader = target "reader"
        do! register actor 3 reader

        let! opened =
            actor.Execute(request 10 "synthetic-a" 0L (BusinessCommand.Open registration), token)

        do!
            rewrite database (accepted opened).Position.Sequence (fun value ->
                { value with
                    Metadata = { value.Metadata with ActorId = id 3 }
                })

        let! _ = verified database
        return ()
    }

let private precedence (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger SET previous_hash=set_byte(previous_hash,0,(get_byte(previous_hash,0)+1)%256),payload=set_byte(payload,20,42) WHERE sequence=4"
                [||]

        do! finding VerificationFinding.Link database
    }

let private malformedProjection (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            sql
                database
                "UPDATE claimcore.actors SET issuer='not-an-issuer' WHERE actor_id=$1"
                [| box (id 1) |]

        do! finding VerificationFinding.AuthorityProjection database
    }

let private priorCaseRequired (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        let altered =
            authorityRequest
                (id 900000)
                database.Fixture.Owner
                (AuthorityAct.SetGrant(
                    Role.PaymentRecorder,
                    GrantTarget.CaseReference "synthetic-a",
                    true
                ))
            |> AuthorityRequests.toRecord

        do! rewrite database 3L (updateAuthority altered)
        let caseId = CaseReferenceId.encode database.Fixture.InstallationId "synthetic-a"

        do!
            sql
                database
                "UPDATE claimcore.grants SET case_id=$1 WHERE actor_id=$2 AND role=$3"
                [| box caseId; box (id 1); box (RoleTokens.token Role.PaymentRecorder) |]

        do! finding VerificationFinding.AuthorityFold database
    }

let private malformedInstant (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            sql
                database
                "ALTER TABLE claimcore.ledger DROP CONSTRAINT ledger_recorded_at_check"
                [||]

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger SET recorded_at='infinity' WHERE sequence=4"
                [||]

        do! finding VerificationFinding.PayloadFormat database
    }

let private malformedOptionalDate (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let decision: DecisionInput =
            {
                PaymentDecisionDate = "2026-08-15"
                PayableAmount = "1"
                PayableCurrency = "EUR"
            }

        let! decided =
            actor.Execute(request 11 "synthetic-a" 1L (BusinessCommand.Decide decision), token)

        accepted decided |> ignore
        let! _ = verified database

        do!
            sql
                database
                "ALTER TABLE claimcore.cases DROP CONSTRAINT cases_payment_date_check; UPDATE claimcore.cases SET payment_date='infinity'"
                [||]

        do! finding VerificationFinding.CaseProjection database
    }

let private malformedAmount (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            sql
                database
                "ALTER TABLE claimcore.cases DROP CONSTRAINT claimed_money; UPDATE claimcore.cases SET claimed_amount='NaN'"
                [||]

        do! finding VerificationFinding.CaseProjection database
    }

let private ioFailure (database: PostgresFixture.Database) =
    task {
        let! _ = populated database
        do! database.Container.StopAsync()
        let mutable failure = None

        try
            let! _ = Verification.verify database.OwnerSource None token in ()
        with error ->
            failure <- Some(AdministrationFailures.classify error)

        Expect.equal
            failure
            (Some AdministrationFailure.Failed)
            "Unavailable database stays an I/O failure, not divergence"
    }

[<Tests>]
let tests =
    testList
        "typed ledger verification"
        [
            testCase "all seventeen finding identifiers are explicitly covered" (fun () ->
                Expect.equal findingCases.Length 17 "Closed finding vocabulary"

                Expect.equal
                    (findingCases |> List.map fst |> Set.ofList)
                    (VerificationFindings.all |> List.map fst |> Set.ofList)
                    "Every finding has an independent real-boundary case")
            for code, check in findingCases do
                testCaseAsync
                    (VerificationFindings.token code + " has an exact real-boundary control")
                    (run check)
            testCaseAsync "disabled historical executors are divergence" (run disabledExecutor)
            testCaseAsync
                "human-only grant targets remain a permanent authority invariant"
                (run serviceGrantTarget)
            testCaseAsync
                "authority history cannot remove the last enabled human installation owner"
                (run lastOwner)
            testCaseAsync
                "verification does not replay today's case capability matrix"
                (run permanentPolicyOnly)
            testCaseAsync
                "the first finding has stable precedence under multiple damaged properties"
                (run precedence)
            testCaseAsync
                "malformed actor projections are divergence rather than I/O failure"
                (run malformedProjection)
            testCaseAsync
                "case-scoped authority is resolved against earlier ledger cases, not final projections"
                (run priorCaseRequired)
            testCaseAsync
                "malformed retained instants are typed payload divergence"
                (run malformedInstant)
            testCaseAsync
                "an invalid optional projection date cannot disappear as NULL"
                (run malformedOptionalDate)
            testCaseAsync "malformed numeric projections are typed divergence" (run malformedAmount)
            testCaseAsync "real database I/O failure remains FAILED" (run ioFailure)
        ]
