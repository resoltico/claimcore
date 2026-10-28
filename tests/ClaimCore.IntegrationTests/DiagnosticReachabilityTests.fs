module ClaimCore.IntegrationTests.DiagnosticReachabilityTests

open ClaimCore.Protocol

open ClaimCore.TestSupport

open ClaimCore.Json

open System
open System.Threading
open System.Text.Json
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.ApplicationFixtures
open ClaimCore.IntegrationTests.DiagnosticWitnesses
open ClaimCore.IntegrationTests.DiagnosticEvidence


let private completeVocabulary () =
    async {
        let! scalar = scalarEvidence () |> Async.AwaitTask

        let! states =
            stateScenarios () |> List.map stateEvidence |> Task.WhenAll |> Async.AwaitTask

        let! boundaries = boundaryEvidence () |> Async.AwaitTask

        let evidence =
            scalar @ List.ofArray states @ boundaries |> List.collect (fun items -> items)

        let identifiers = evidence |> List.map fst |> Set.ofList

        let baselineCodes = baseline.Value.Keys |> Seq.map fst |> Set.ofSeq

        Expect.equal
            (Set.ofList (
                DiagnosticCodecs.codes
                @ (DiagnosticCodecs.violationDefinitions |> List.map _.Id)
            ))
            baselineCodes
            "The codec preserves captured refusal semantics and includes principal and role admission."

        let produced = Set.ofList evidence

        Expect.isEmpty
            (Set.difference (baseline.Value.Keys |> Set.ofSeq) produced |> Set.toList)
            (sprintf
                "Captured code/target witnesses missing: %A"
                (Set.difference (baseline.Value.Keys |> Set.ofSeq) produced))

        let expected =
            DiagnosticCodecs.codes
            @ (DiagnosticCodecs.violationDefinitions |> List.map _.Id)
            |> Set.ofList

        Expect.isEmpty
            (Set.difference expected identifiers |> Set.toList)
            (sprintf
                "Public diagnostics without a request witness: %A"
                (Set.difference expected identifiers |> Set.toList))

        Expect.isEmpty
            (Set.difference identifiers expected |> Set.toList)
            "Requests never emit a diagnostic outside the public vocabulary"

        let fields = evidence |> List.choose snd |> Set.ofList

        let expectedFields =
            // Closed kind/role enums are admitted by the protocol, not refused by Application.
            (InputTarget.all
             |> List.filter (fun (target, _) ->
                 target <> InputTarget.Role && target <> InputTarget.PrincipalKind)
             |> List.map snd)
            @ [ "limit"; "cursor" ]
            |> Set.ofList

        Expect.equal
            fields
            expectedFields
            "Every business-refusable field has an Application request witness"
    }

let private missingCasePrivacy () =
    async {
        let! _, fixture = fixture () |> Async.AwaitTask

        use _owned =
            { new System.IDisposable with
                member _.Dispose() = fixture.Dispose()
            }

        let! actor = caseWorker fixture |> Async.AwaitTask

        let acts =
            stateScenarios ()
            |> List.collect (fun (setup, final) -> final :: setup)
            |> List.filter (fun act -> CaseAct.kind act <> CaseActKind.Open)

        let kinds = acts |> List.map CaseAct.kind |> Set.ofList

        let expected =
            CaseActKind.all
            |> List.filter (fun kind -> kind <> CaseActKind.Open)
            |> Set.ofList

        Expect.equal kinds expected "Every non-OPEN operation is exercised against an absent case"

        for index, act in List.indexed acts do
            let! outcome =
                execute actor (actRequest (100 + index) "MISSING" 1L act) |> Async.AwaitTask

            Expect.equal
                (refused outcome |> List.head |> fst)
                "RESOURCE_UNAVAILABLE"
                "Missing case stays private"

        Expect.isFalse
            (DiagnosticCodecs.codes |> List.contains "CASE_NOT_FOUND")
            "Domain-only missing state has no public diagnostic"
    }

[<Tests>]
let tests =
    testList
        "public refusal reachability"
        [
            testCaseAsync
                "all diagnostics and fields have actual Application request witnesses"
                (completeVocabulary ())
            testCaseAsync "absent case execution preserves resource privacy" (missingCasePrivacy ())
        ]
