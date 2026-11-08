module ClaimCore.IntegrationTests.OperationLookupDisclosureTests

open ClaimCore.TestSupport

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private run action =
    async {
        let! database = PostgresFixture.create () |> Async.AwaitTask

        try
            do! action database |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

let private lookup (actor: ServiceContext) operation =
    task {
        let! reply = Service.operationGet actor { OperationId = operation } token
        return reply.Outcome
    }

let private revokeReadRoles fixture (actor: ServiceContext) principal =
    task {
        for operation, role in
            [ id fixture 111, Role.CaseHandler; id fixture 114, Role.PaymentRecorder ] do
            let! reply =
                grant
                    actor
                    operation
                    principal
                    {
                        Role = role
                        Scope = GrantTarget.Installation
                    }
                    false

            acceptedAuthority reply
    }

let private receipts (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let! created =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request
                        database.Fixture
                        110
                        "LOOKUP-DISCLOSURE"
                        0L
                        (AuthoredCaseAct.Open registration)
                ))
                (token))

        let receipt = accepted created
        let! resolved = lookup actor (id database.Fixture 110)

        match resolved with
        | Outcome.Succeeded(OperationLookupResult.Accepted(actual, true)) ->
            expectReceipt actual receipt "Committed operation returns its receipt"
        | other -> failtestf "Expected resolved acceptance: %A" other

        do! revokeReadRoles database.Fixture actor database.Fixture.Owner

        let! withheld = lookup actor (id database.Fixture 110)

        match withheld with
        | Outcome.Succeeded(OperationLookupResult.Accepted(receipt, true)) ->
            Expect.equal
                (caseReceipt receipt).Snapshot
                SnapshotDisclosure.Withheld
                "Author without read access still learns acceptance"
        | _ -> failtest "Author must receive a receipt"

        let! steward =
            grant
                actor
                (id database.Fixture 112)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority steward

        let! erased =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request
                        database.Fixture
                        113
                        "LOOKUP-DISCLOSURE"
                        1L
                        (AuthoredCaseAct.Bare BareAct.Erase)
                ))
                (token))

        accepted erased |> ignore
        let! resolvedErased = lookup actor (id database.Fixture 110)

        match resolvedErased with
        | Outcome.Succeeded(OperationLookupResult.Accepted(receipt, true)) ->
            Expect.equal
                (caseReceipt receipt).Snapshot
                SnapshotDisclosure.Erased
                "Erasure remains visible without payload"
        | _ -> failtest "Erased author receipt must remain available"
    }

[<Tests>]
let tests =
    testList
        "Operation receipt disclosure"
        [
            testCaseAsync "author receipts follow current disclosure and erasure" (run receipts)
        ]
