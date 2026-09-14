module ClaimCore.IntegrationTests.TransactionPaginationTests

open System
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.IntegrationTests.Fixtures

let private insertBatchSize = 8

let private references (page: CasePage) =
    page.Items |> List.map (Claim.view >> fun view -> view.Fields.CaseReference)

let private openCase service reference =
    let request =
        { newRequest () with
            CaseReference = reference
        }

    Service.executeAsync service clock request

let private openBatch service references =
    references
    |> List.map (openCase service)
    |> List.toArray
    |> Task.WhenAll
    |> await
    |> Array.iter (accepted >> ignore)

let private verifyPagination () =
    use database = store ()
    let service = database :> IClaimStore
    let prefix = "zz-page-" + Guid.NewGuid().ToString("N") + "-"
    let expected = [ for index in 0..50 -> prefix + $"{index:D2}" ]

    expected |> List.chunkBySize insertBatchSize |> List.iter (openBatch service)

    let first = service.List(Some prefix) |> await |> accepted
    Expect.equal first.Items.Length 50 "Page size"
    Expect.equal (references first) (expected |> List.take 50) "C-collation order"

    let cursor =
        first.NextAfter
        |> Option.defaultWith (fun () -> failtest "Expected continuation")

    let second = service.List(Some cursor) |> await |> accepted
    Expect.equal (references second) [ expected[50] ] "No duplicate or skipped boundary item"
    Expect.isNone second.NextAfter "Sequence ends"

let tests =
    testCase
        "case pagination has an exact fifty-item boundary and lossless continuation"
        verifyPagination
