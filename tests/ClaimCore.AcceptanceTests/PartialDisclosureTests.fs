module ClaimCore.AcceptanceTests.PartialDisclosureTests

open Expecto

[<Tests>]
let tests =
    testCaseAsync
        "published partial updates retain revision and disclosure guards"
        (async { do! CliDisclosure.published () |> Async.AwaitTask })
