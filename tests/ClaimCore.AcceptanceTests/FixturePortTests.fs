module ClaimCore.AcceptanceTests.FixturePortTests

open System.Net
open System.Net.Sockets
open Expecto

[<Tests>]
let tests =
    testCase "owned service ports avoid dynamic allocation and concurrent fixture reuse" (fun () ->
        let allocated = [| 1..32 |] |> Array.Parallel.map (fun _ -> FixtureFiles.port ())

        Expect.equal
            (Set.ofArray allocated |> Set.count)
            allocated.Length
            "Concurrent fixtures receive distinct ports"

        for value in allocated do
            Expect.isTrue
                (value >= 10000 && value < 32768)
                "Service ports exclude the container dynamic range"

            use listener = new TcpListener(IPAddress.Loopback, value)
            listener.ExclusiveAddressUse <- true
            listener.Start())
