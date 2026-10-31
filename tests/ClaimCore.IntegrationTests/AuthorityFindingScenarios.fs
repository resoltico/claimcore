module ClaimCore.IntegrationTests.AuthorityFindingScenarios

open ClaimCore.TestSupport

open ClaimCore.Protocol

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport
open ClaimCore.IntegrationTests.VerificationScenarioInputs
open ClaimCore.IntegrationTests.LedgerFindingScenarios


let internal disabledExecutor (database: PostgresFixture.Database) =
    task {
        let! actor = populated database
        let reader = target "disabled"
        do! register actor 3 reader

        let! disabled =
            (Service.execute
                actor
                (OperationInput.Authority(authorityRequest (id 4) reader (AuthorityAct.Disable)))
                (token))

        acceptedAuthority disabled

        let! opened =
            (Service.execute
                actor
                (OperationInput.Case(request 11 "another" 0L (AuthoredCaseAct.Open registration)))
                (token))

        do!
            rewrite database (accepted opened).Position.Sequence (fun value ->
                { value with
                    Metadata =
                        { value.Metadata with
                            ActorId = (CaseFixtures.actorId (id 3))
                        }
                })

        do! finding VerificationFinding.ActorNotEnabled database
    }

let internal serviceGrantTarget (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let service =
            PrincipalKey.service "https://issuer.example/" "service"
            |> Result.defaultWith (string >> failtest)

        do! register actor 3 service

        let! granted =
            grant
                actor
                (id 4)
                service
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                true

        let altered =
            authorityRequest
                (id 4)
                service
                (AuthorityAct.Grant
                    {
                        Role = Role.Owner
                        Scope = GrantTarget.Installation
                    })
            |> Authority.parse
            |> Result.defaultWith (fun _ -> failtest "Synthetic authority input must admit")
            |> AuthorityRequests.toRecord

        do! rewrite database (accepted granted).Position.Sequence (updateAuthority altered)
        do! finding VerificationFinding.HumanRequired database
    }

let internal lastOwner (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let! unchanged =
            (Service.execute
                actor
                (OperationInput.Authority(
                    authorityRequest (id 20) database.Fixture.Owner (AuthorityAct.Enable)
                ))
                (token))

        let altered =
            authorityRequest (id 20) database.Fixture.Owner (AuthorityAct.Disable)
            |> Authority.parse
            |> Result.defaultWith (fun _ -> failtest "Synthetic authority input must admit")
            |> AuthorityRequests.toRecord

        do!
            rewrite
                database
                (accepted unchanged).Position.Sequence
                (authoritySubject (fun subject ->
                    { subject with
                        Act = AuthorityKind.Disable
                    })
                 >> updateAuthority altered)

        do! finding VerificationFinding.AuthorityFold database
    }

let internal permanentPolicyOnly (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        let reader = target "reader"
        do! register actor 3 reader

        let! opened =
            (Service.execute
                actor
                (OperationInput.Case(
                    request 10 "synthetic-a" 0L (AuthoredCaseAct.Open registration)
                ))
                (token))

        do!
            rewrite database (accepted opened).Position.Sequence (fun value ->
                { value with
                    Metadata =
                        { value.Metadata with
                            ActorId = (CaseFixtures.actorId (id 3))
                        }
                })

        let! _ = verified database
        return ()
    }

let internal precedence (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger SET previous_hash=set_byte(previous_hash,0,(get_byte(previous_hash,0)+1)%256),payload=set_byte(payload,20,42) WHERE sequence=4"
                [||]

        do! finding VerificationFinding.Link database
    }

let internal priorCaseRequired (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        let altered =
            authorityRequest
                (id 900000)
                database.Fixture.Owner
                (AuthorityAct.Grant
                    {
                        AuthoredGrant.Role = Role.PaymentRecorder
                        Scope = GrantTarget.CaseReference "synthetic-a"
                    })
            |> Authority.parse
            |> Result.defaultWith (fun _ -> failtest "Synthetic authority input must admit")
            |> AuthorityRequests.toRecord

        do! rewrite database (CaseFixtures.sequence (3L)) (updateAuthority altered)
        let caseId = CaseFixtures.caseId (id 10)

        do!
            sql
                database
                "UPDATE claimcore.grants SET case_id=$1 WHERE actor_id=$2 AND role=$3"
                [|
                    box (CaseId.value caseId)
                    box (id 1)
                    box (Role.token Role.PaymentRecorder)
                |]

        do! finding VerificationFinding.AuthorityFold database
    }
