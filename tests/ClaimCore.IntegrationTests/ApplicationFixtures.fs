module internal ClaimCore.IntegrationTests.ApplicationFixtures

open ClaimCore.TestSupport
open ClaimCore.Protocol

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
    |> Result.defaultWith (string >> failtest)

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
type AfterCaseWriteStore(inner: IStore, afterWrite: unit -> Task<unit>) =
    let mutable reads = 0
    member _.ReadCalls = reads

    interface IStore with
        member _.Read(work, ct) =
            reads <- reads + 1
            inner.Read(work, ct)

        member _.Write(work, ct) =
            task {
                let! result = inner.Write(work, ct)
                do! afterWrite ()
                return result
            }
