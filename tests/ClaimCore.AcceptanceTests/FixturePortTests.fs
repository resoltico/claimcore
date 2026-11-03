module ClaimCore.AcceptanceTests.FixturePortTests

open System.Net
open System.Net.Sockets
open Expecto

[<Tests>]
let tests =
    testCase
        "owned service ports avoid browser restrictions, dynamic allocation and concurrent fixture reuse"
        (fun () ->
            // Cross the restricted port even when this test runs alone from a cold allocator.
            let allocated = [| 1..96 |] |> Array.Parallel.map (fun _ -> FixtureFiles.port ())

            Expect.equal
                (Set.ofArray allocated |> Set.count)
                allocated.Length
                "Concurrent fixtures receive distinct ports"

            for value in allocated do
                Expect.notEqual
                    value
                    10080
                    "Chromium must be able to navigate to every service port"

                Expect.isTrue
                    (value >= 10000 && value < 32768)
                    "Service ports exclude the container dynamic range"

                use listener = new TcpListener(IPAddress.Loopback, value)
                listener.ExclusiveAddressUse <- true
                listener.Start())
