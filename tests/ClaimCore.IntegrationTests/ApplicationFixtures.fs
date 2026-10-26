module internal ClaimCore.IntegrationTests.ApplicationFixtures

open ClaimCore.TestSupport
open ClaimCore.Contract

open System
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures

let instant = DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)
let installation = Guid.Parse "90000000-0000-4000-8000-000000000001"

let owner =
    PrincipalKey.human "https://issuer.example/" "owner"
    |> Result.defaultWith failtest

let calendar =
    { new IBusinessCalendar with
        member _.TimeZoneId = "Etc/UTC"
        member _.DateAt value = DateOnly.FromDateTime value.UtcDateTime
    }

let fixture () =
    task {
        let! database = PostgresFixture.createFor owner
        return database, database.Fixture
    }

let run action =
    async {
        let! database, value = fixture () |> Async.AwaitTask

        try
            do! action database value |> Async.AwaitTask
        finally
            value.Dispose()
    }

let openRequest n reference =
    request n reference 0L (UnvalidatedCaseAct.Open registration)

/// A write can finish before another accepted change is observed by its caller.
type AfterCaseWriteStore(inner: ILedgerStore, afterWrite: unit -> Task<unit>) =
    let mutable reads = 0
    member _.ReadCalls = reads

    interface ILedgerStore with
        member _.ResolveOperation(principal, id, decide, ct) =
            inner.ResolveOperation(principal, id, decide, ct)

        member _.WriteCase(principal, operationId, reference, decide, ct) =
            task {
                let! result = inner.WriteCase(principal, operationId, reference, decide, ct)
                do! afterWrite ()
                return result
            }

        member _.WriteAuthority(principal, operationId, target, reference, decide, ct) =
            inner.WriteAuthority(principal, operationId, target, reference, decide, ct)

        member _.ReadAuthorityTarget(principal, operationId, target, reference, ct) =
            reads <- reads + 1
            inner.ReadAuthorityTarget(principal, operationId, target, reference, ct)

        member _.ReadCase(principal, operationId, reference, ct) =
            reads <- reads + 1
            inner.ReadCase(principal, operationId, reference, ct)

        member _.ReadActor(principal, ct) =
            reads <- reads + 1
            inner.ReadActor(principal, ct)

        member _.ReadOperation(principal, operationId, ct) =
            reads <- reads + 1
            inner.ReadOperation(principal, operationId, ct)

        member _.ListCases(principal, readRoles, select, limit, ct) =
            reads <- reads + 1
            inner.ListCases(principal, readRoles, select, limit, ct)

        member _.ReadHistory(principal, reference, select, limit, ct) =
            reads <- reads + 1
            inner.ReadHistory(principal, reference, select, limit, ct)

        member _.ListGrants(principal, target, select, limit, ct) =
            reads <- reads + 1
            inner.ListGrants(principal, target, select, limit, ct)

        member _.ListActors(principal, select, limit, ct) =
            reads <- reads + 1
            inner.ListActors(principal, select, limit, ct)
