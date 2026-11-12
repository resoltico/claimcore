module ClaimCore.IntegrationTests.RootVerificationTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private corrupt expected statement =
    run (fun database ->
        task {
            let! _ = populated database
            do! guardedSql database statement [||]
            do! finding expected database
        })

let private rehashedCase (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let! closed =
            Service.execute
                actor
                (Inputs.caseOperation (
                    request database.Fixture 11 "synthetic-a" 1L (CaseAct.Bare BareAct.Close)
                ))
                token

        accepted closed |> ignore
        let! original = row database (CaseFixtures.sequence 4L)

        let values =
            match original.Values with
            | RetainedOperationValues.Case(Some value) -> value
            | _ -> failtest "Available accepted case required"

        let after =
            { values.Decision.After with
                State =
                    CaseState.map
                        (fun fields ->
                            { fields with
                                Status = CaseStatus.Closed
                            })
                        values.Decision.After.State
            }

        let nonce =
            original.Root.Structure.Compartments
            |> List.exactlyOne
            |> Compartment.content
            |> Option.get
            |> fst

        do!
            rewrite database original.Root.Header.Sequence (fun retained ->
                recreate
                    retained.Root.Header
                    retained.Root.Structure.Command
                    (OperationValues.Case
                        { values with
                            Decision = { values.Decision with After = after }
                        })
                    nonce)

        do!
            sql
                database
                "UPDATE claimcore.cases SET snapshot=$1,status='CLOSED' WHERE case_id=$2"
                [|
                    box (
                        CaseRecord.encodeSnapshot
                            { after with
                                Revision = CaseFixtures.revision 2L
                            }
                    )
                    box (CaseId.value after.CaseId)
                |]

        do! finding VerificationFinding.StateSequence database
    }

let private actors (database: PostgresFixture.Database) =
    task {
        let! owner = caseWorker database.Fixture

        let principal =
            PrincipalKey.human "https://issuer.example/" "disabled-root-executor"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            Service.execute
                owner
                (OperationInput.Authority(
                    Inputs.authorityRequest
                        (id database.Fixture 20)
                        principal
                        (AuthorityAct.Register Inputs.actorProfile)
                ))
                token

        acceptedAuthority registered

        let! disabled =
            Service.execute
                owner
                (OperationInput.Authority(
                    Inputs.authorityRequest (id database.Fixture 21) principal AuthorityAct.Disable
                ))
                token

        acceptedAuthority disabled

        let! opened =
            Service.execute
                owner
                (Inputs.caseOperation (
                    request database.Fixture 22 "executor-root" 0L (CaseAct.Open registration)
                ))
                token

        let receipt = accepted opened
        let! _ = verified database

        do!
            rewrite database receipt.Position.Sequence (fun retained ->
                OperationRoot.append
                    { retained.Root.Header with
                        ActorId =
                            ActorId.ofOperationId (
                                CaseFixtures.operationId (id database.Fixture 20)
                            )
                    }
                    retained.Root.Structure.Command
                    retained.Root.Structure.Effects
                    retained.Root.Structure.Compartments
                |> Result.defaultWith (fun error -> failtestf "Actor root: %A" error))

        do! finding VerificationFinding.ActorNotEnabled database
    }

[<Tests>]
let tests =
    testList
        "current operation verification negatives"
        [
            testCaseAsync
                "link corruption precedes root interpretation"
                (corrupt
                    VerificationFinding.Link
                    "UPDATE claimcore.ledger SET previous_hash=set_byte(previous_hash,0,(get_byte(previous_hash,0)+1)%256) WHERE sequence=4")
            testCaseAsync
                "wrong stored root hash cannot enter replay"
                (corrupt
                    VerificationFinding.PayloadFormat
                    "UPDATE claimcore.ledger SET entry_hash=set_byte(entry_hash,0,(get_byte(entry_hash,0)+1)%256) WHERE sequence=4")
            testCaseAsync
                "changed salt cannot borrow an accepted compartment commitment"
                (corrupt
                    VerificationFinding.PayloadFormat
                    "UPDATE claimcore.ledger_compartments SET nonce=set_byte(nonce,0,(get_byte(nonce,0)+1)%256) WHERE operation_sequence=4")
            testCaseAsync
                "unknown public command members are not silently normalized away"
                (corrupt
                    VerificationFinding.PayloadFormat
                    "UPDATE claimcore.ledger SET command=command || '{\"unknownMember\":null}'::jsonb WHERE sequence=4")
            testCaseAsync
                "rehashed case result and matching projection still require independent replay"
                (run rehashedCase)
            testCaseAsync
                "a rehashed accepted command cannot borrow a disabled executor"
                (run actors)
        ]
