module ClaimCore.Tests.ContractTests

open ClaimCore.Cli

open ClaimCore.Protocol
open ClaimCore.Json

open System
open System.Text
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Application
open ClaimCore.TestSupport
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures
open ClaimCore.Tests.ContractScenarioInputs
open ClaimCore.Tests.OutcomeContractScenarios
open ClaimCore.Tests.InputContractScenarios


[<Tests>]
let tests =
    testList
        "service contract"
        [
            testCase
                "technical authority inputs retain owned presentation"
                authorityFieldPresentation
            testCase
                "principal syntax and authoritative identity admission remain distinct"
                principalAdmission
            testCase "text policy remains a nonvalidating presentation hint" policyHints
            testCase
                "retained text renders and reaffirms while changed policy violations refuse"
                retainedText
            testCase "both mutation scopes share outcomes and exit codes" executeOutcomes
            testCase "withheld and erased snapshots remain distinct" disclosures
            testCase "query views and context encode through typed outcomes" (fun () ->
                queryCodecs ()
                contextCodec ())
            testCase "preview encodes review and replay outcomes" previewCodecs
            testCase "closed diagnostics have presentation and no coarse codes" diagnostics
            testCase "rejections retain core recovery guidance" actionEnums
            testCase "rejections retain wire recovery guidance" actionTokens
            testCase "strict frames reject hostile shapes and preserve original bytes" rejectFrames
            testCase "wire integers enforce lexemes and safe boundaries" integerLexemes
            testCase "all frame and HTTP decoders are total for bounded hostile bytes" (fun () ->
                PropertyHarness.run "invocation byte decoder" decoderProperty)
        ]
