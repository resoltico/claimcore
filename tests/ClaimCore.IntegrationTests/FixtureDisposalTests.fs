module ClaimCore.IntegrationTests.FixtureDisposalTests

open System
open System.IO
open Expecto
open ClaimCore.TestSupport

let private captured action =
    try
        action ()
        None
    with error ->
        Some error

[<Tests>]
let tests =
    testList
        "owned PostgreSQL fixture disposal"
        [
            testCase "successful cleanup rethrows the exact setup failure" (fun () ->
                let primary = IOException("Synthetic setup failure")
                let mutable disposed = false

                let failure =
                    captured (fun () ->
                        FixtureDisposal.afterFailure primary [ (fun () -> disposed <- true) ])

                Expect.isTrue disposed "The owned resource is disposed"

                Expect.isTrue
                    (failure |> Option.exists (fun error -> obj.ReferenceEquals(error, primary)))
                    "Original failure identity remains visible")
            testCase
                "every owned disposal runs and all setup and teardown failures survive"
                (fun () ->
                    let primary = IOException("Synthetic setup failure")
                    let teardown = IOException("Synthetic source disposal failure")
                    let mutable finalDisposed = false

                    let failure =
                        captured (fun () ->
                            FixtureDisposal.afterFailure
                                primary
                                [ (fun () -> raise teardown); (fun () -> finalDisposed <- true) ])

                    Expect.isTrue
                        finalDisposed
                        "Container disposal runs after source disposal fails"

                    match failure with
                    | Some(:? AggregateException as errors) ->
                        Expect.equal errors.InnerExceptions.Count 2 "Both failures remain visible"

                        Expect.isTrue
                            (obj.ReferenceEquals(errors.InnerExceptions[0], primary))
                            "Setup first"

                        Expect.isTrue
                            (obj.ReferenceEquals(errors.InnerExceptions[1], teardown))
                            "Cleanup retained"
                    | _ -> failtest "Setup and disposal failures must both remain visible")
        ]
