module ClaimCore.WebTests.RequestLogTests

open ClaimCore.Wire

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Web
open ClaimCore.WebTests.FacadeStub
open ClaimCore.WebTests.TestServerFixture

let private lines (text: string) =
    text.Split('\n', StringSplitOptions.RemoveEmptyEntries)

let private names (element: JsonElement) =
    element.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

let private canaryLog () =
    task {
        use host = new Host(true)
        let! _ = host.Login()
        let! token = host.Token()
        host.ClearLog()
        let canary = "sensitive-canary-" + Guid.NewGuid().ToString("N")
        let input = Encoding.UTF8.GetBytes("{\"caseReference\":\"" + canary + "\"}")
        let! status, _ = host.Call("case.get", input, token)
        Expect.equal status 200 "Reference-bearing request"
        Expect.equal (lines host.Log).Length 1 "Exactly one request line"
        use entry = JsonDocument.Parse((lines host.Log)[0])

        Expect.equal
            (names entry.RootElement)
            (set [ "endpoint"; "outcome"; "status"; "durationMs"; "actorId"; "operationId" ])
            "Closed field set"

        Expect.isFalse (host.Log.Contains canary) "No case reference"
        host.ClearLog()
        host.Facade.Error <- Some(InvalidOperationException canary)
        let! status, _ = host.Call("case.get", input, token)
        Expect.equal status 500 "Unexpected failure"
        Expect.isFalse (host.Log.Contains canary) "No body or exception message"
        let entries = lines host.Log |> Array.map (fun text -> JsonDocument.Parse text)

        try
            Expect.equal entries.Length 2 "One sanitized exception and one request"

            let error =
                entries
                |> Array.find (fun entry -> entry.RootElement.TryGetProperty("kind") |> fst)

            Expect.equal
                (names error.RootElement)
                (set [ "kind"; "exceptionType"; "stack" ])
                "Exception contains only type and method stack"

            Expect.isGreaterThan
                (error.RootElement.GetProperty("stack").GetArrayLength())
                0
                "Useful stack evidence"
        finally
            entries |> Array.iter _.Dispose()
    }
    |> _.GetAwaiter().GetResult()

let private actorIdentity () =
    task {
        use host = new Host(true)
        let! _ = host.Login()
        let! token = host.Token()
        let endpoint = Endpoint.CaseExecute
        let input = Array.copy commandInput

        for outcome, known in
            [
                ExecuteOutcome.Accepted(receipt operationId "WEB-001"), true
                ExecuteOutcome.AlreadyAccepted(receipt operationId "WEB-001"), false
            ] do
            host.ClearLog()
            host.Facade.Execute <- (fun _ -> Task.FromResult outcome)
            let! status, _ = host.Call(Endpoint.id endpoint, input, token)
            Expect.equal status 200 "Mutation"
            use entry = JsonDocument.Parse((lines host.Log)[0])

            Expect.equal
                (entry.RootElement.GetProperty("actorId").ValueKind = JsonValueKind.String)
                known
                "Only a fresh acceptance proves the current actor ID"

            Expect.equal
                (entry.RootElement.GetProperty("operationId").ValueKind)
                JsonValueKind.String
                "Validated request identity"
    }
    |> _.GetAwaiter().GetResult()

[<Tests>]
let tests =
    testList
        "request log"
        [
            testCase "closed request lines and exception stacks never contain the canary" canaryLog
            testCase "replayed authors are not misidentified as current callers" actorIdentity
        ]
    |> testSequenced
