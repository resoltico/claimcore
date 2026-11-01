module ClaimCore.WebTests.FrameworkExceptionLogTests

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.Web
open Microsoft.Extensions.Logging

[<Tests>]
let tests =
    testCase
        "framework error diagnostics exclude category, state, formatter and exception text"
        (fun () ->
            use sink = new StringWriter()
            use provider = new FrameworkExceptionLog(sink)
            let logger = (provider :> ILoggerProvider).CreateLogger "PRIVATE_CATEGORY_CANARY"
            let failure = InvalidOperationException "PRIVATE_EXCEPTION_CANARY"

            let formatter =
                Func<string, exn | null, string>(fun _ _ ->
                    failtest "Private framework formatting must not run")

            logger.Log(LogLevel.Information, EventId 1, "PRIVATE_STATE_CANARY", failure, formatter)
            Expect.equal (sink.ToString()) "" "Non-error framework events are excluded"
            logger.Log(LogLevel.Error, EventId 2, "PRIVATE_STATE_CANARY", failure, formatter)
            use line = JsonDocument.Parse(sink.ToString())

            Expect.equal
                (line.RootElement.GetProperty("exceptionType").GetString())
                "System.InvalidOperationException"
                "The safe failure category is actually reported"

            Expect.equal
                (line.RootElement.GetProperty("kind").GetString())
                "exception"
                "The existing diagnostic representation is reused"

            Expect.isFalse
                (sink.ToString().Contains "PRIVATE_")
                "No category, provider input, state or private message is retained"

            Expect.isTrue (logger.IsEnabled LogLevel.Error) "Errors are observed"
            Expect.isFalse (logger.IsEnabled LogLevel.None) "None does not enable diagnostics")
