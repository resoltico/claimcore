module ClaimCore.Tests.CliTransportTests

open ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Json


open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Cli
open CliTransportFixture
open ClaimCore.Tests.TransportFailureScenarios
open ClaimCore.Tests.TransportBodyScenarios


[<Tests>]
let tests =
    testList
        "CLI service transport"
        [
            testCase "response bounds media status and cancellation guards reject" (fun () ->
                responseBounds ()
                dispatchGuards ()
                mediaVariants ()
                mixedMediaRefusals ()
                absentJsonMedia ()
                mixedCaseJsonMedia ())
            testCase
                "malformed business strings reach typed server rejection unchanged"
                malformedBusinessInput
            testCase
                "HTTP bodies near the byte cap cannot create oversized reply frames"
                frameResponseBounds
            testCase "all result tags select the specified exit" resultExits
            testCase "catalogue methods handshake and exact input bytes are sent" requestBytes
            testCase "all typed service failure phases preserve mutation knowledge" failurePhases
            testCase "invalid replies and forged status never settle a mutation" invalidReplies
            testCase
                "local cancellation unsupported invocation and pre-dispatch failure exits"
                localExits
        ]
