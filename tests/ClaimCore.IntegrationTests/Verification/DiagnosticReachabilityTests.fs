module ClaimCore.IntegrationTests.DiagnosticReachabilityTests

open ClaimCore.Wire
open ClaimCore.Presentation

open ClaimCore.TestSupport

open ClaimCore.Json

open System
open System.Threading
open System.Text.Json
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.ApplicationFixtures
open ClaimCore.IntegrationTests.DiagnosticWitnesses
open ClaimCore.IntegrationTests.DiagnosticEvidence


let private completeVocabulary () =
    async {
        let! scalar = scalarEvidence () |> Async.AwaitTask

        let! states = stateEvidence () |> Async.AwaitTask
        let! boundaries = boundaryEvidence () |> Async.AwaitTask
        let! entries = DiagnosticEntryEvidence.entriesEvidence () |> Async.AwaitTask
        let! associations = DiagnosticEntryEvidence.associationEvidence () |> Async.AwaitTask

        let! authored =
            DiagnosticEntryEvidence.authoredIdentityAndClosureEvidence () |> Async.AwaitTask

        let! attachments = DiagnosticEntryEvidence.attachmentEvidence () |> Async.AwaitTask

        let evidence =
            scalar @ states @ boundaries @ entries @ associations @ authored @ attachments
            |> List.collect (fun items -> items)
            |> fun observed -> observed @ DiagnosticEntryEvidence.technicalCountEvidence ()

        let identifiers = evidence |> List.map fst |> Set.ofList

        let baselineCodes = baseline.Value.Keys |> Seq.map fst |> Set.ofSeq

        Expect.equal
            (Set.ofList (
                DiagnosticCodecs.codes
                @ (DiagnosticCodecs.violationDefinitions |> List.map _.Id)
            ))
            baselineCodes
            (sprintf
                "Public diagnostic inventory differs: unrecorded=%A obsolete=%A"
                (Set.difference
                    (Set.ofList (
                        DiagnosticCodecs.codes
                        @ (DiagnosticCodecs.violationDefinitions |> List.map _.Id)
                    ))
                    baselineCodes)
                (Set.difference
                    baselineCodes
                    (Set.ofList (
                        DiagnosticCodecs.codes
                        @ (DiagnosticCodecs.violationDefinitions |> List.map _.Id)
                    ))))

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
            // Closed enums are admitted by the protocol. Technical count saturation is
            // checked at the financial owner and codec independently; other state witnesses use PostgreSQL.
            (InputTarget.all
             |> List.filter (fun (target, _) ->
                 match target with
                 | InputTarget.Role
                 | InputTarget.PrincipalKind
                 | InputTarget.Scope
                 | InputTarget.Group RecordGroup.Registration
                 | InputTarget.Group RecordGroup.Decision -> false
                 | _ -> true)
             |> List.map snd)
            @ [ "limit"; "cursor" ]
            |> Set.ofList

        Expect.equal
            fields
            expectedFields
            (sprintf
                "Missing field witnesses: %A; unexpected: %A"
                (Set.difference expectedFields fields |> Set.toList)
                (Set.difference fields expectedFields |> Set.toList))
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
            |> List.filter (fun (taskName, _) -> taskName <> "case.open")

        let kinds = acts |> List.map fst |> Set.ofList

        let expected =
            [
                "case.amendRegistration"
                "case.correct"
                "case.changeReference"
                "case.decide"
                "case.withdrawDecision"
                "case.recordPayment"
                "case.removePayment"
                "case.void"
                "case.reinstate"
                "case.close"
                "case.reopen"
                "case.erase"
            ]
            |> Set.ofList

        Expect.equal kinds expected "Every non-OPEN operation is exercised against an absent case"

        for index, (taskName, values) in List.indexed acts do
            let! outcome =
                execute actor (request fixture (100 + index) "MISSING" 1L taskName values)
                |> Async.AwaitTask

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
                "diagnostics have request witnesses except independently checked integer saturation"
                (completeVocabulary ())
            testCaseAsync "absent case execution preserves resource privacy" (missingCasePrivacy ())
        ]
