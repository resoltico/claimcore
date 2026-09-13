module ClaimCore.Tests.ExampleContractTests

open System.IO
open Expecto
open ClaimCore.Application
open ClaimCore.Cli
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.Tests.Fixtures

let private names =
    [
        "01-open.json"
        "02-decide.json"
        "03-record-payment.json"
        "04-close.json"
        "05-reopen.json"
        "06-correct-payment-record.json"
    ]

let private exampleDirectory = Path.Combine(RepositoryRoot.find (), "examples")

let private request filename =
    let bytes = File.ReadAllBytes(Path.Combine(exampleDirectory, filename))

    match StrictJson.parseDocument 131072 bytes with
    | Error _ -> failtest "The checked example must be strict CLI-v3 JSON."
    | Ok document ->
        use frame = document

        match InvocationFraming.decode frame.RootElement with
        | Ok(Endpoint.CommandExecute, EndpointInput.Draft draft, None) ->
            Drafts.bind draft
            |> Result.defaultWith (fun _ -> failtest "Example fields must form a Domain command.")
        | _ -> failtest "Every checked example must be a canonical command.execute frame."

let private examplesRunThroughDomain =
    testCase
        "every shipped command example decodes and completes its sequential Domain transition"
        (fun () ->
            let mutable current: Claim option = None

            for name in names do
                let authored = request name
                let canonical = RequestRecord.encode authored

                Expect.equal
                    (RequestRecord.decode 65536 canonical)
                    (Ok authored)
                    "The durable record round-trips each v3 example"

                current <-
                    Claim.decide today authored current
                    |> Result.map Some
                    |> Result.defaultWith (fun _ -> failtest "Example transition must be legal.")

            let final =
                current
                |> Option.map Claim.view
                |> Option.defaultWith (fun () ->
                    failtest "The example lifecycle must create a case.")

            Expect.equal final.Version 6L "Each of six examples advances exactly once"
            Expect.equal final.Fields.Status CaseStatus.Opened "The walkthrough reopens the case"
            Expect.isNone final.Fields.PaymentDate "The last example corrects the payment record")

let tests = testList "published example contract" [ examplesRunThroughDomain ]
