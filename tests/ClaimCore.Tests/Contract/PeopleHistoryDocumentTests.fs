module ClaimCore.Tests.PeopleHistoryDocumentTests

open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport

let private parent = ResponseExamples.caseRead.Snapshot.CaseId

let private entry index facts =
    {
        PeopleHistoryEntry.OperationId = ResponseExamples.receipt.OperationId
        Position = EffectPosition.create Sequence.first index |> Option.get
        OperationHash = EntryHash.zero
        RecordedAt = Inputs.workBasis.ObservedAt
        RecordedBy = ResponseExamples.caseRead.RecordedBy
        Target = ResponseExamples.caseRead.RecordedBy
        Facts = facts
    }

[<Tests>]
let tests =
    testCase "people history destinations bind only the authorized scoped effects" (fun () ->
        let document =
            HumanReadDocuments.history
                {
                    Basis = Inputs.workBasis
                    Entries =
                        [
                            entry
                                0
                                (PeopleHistoryFacts.Granted
                                    {
                                        Role = Role.CaseReader
                                        Scope =
                                            GrantScope.Case(
                                                parent,
                                                ResponseExamples.caseRead.Snapshot.Reference
                                            )
                                    })
                            entry 1 PeopleHistoryFacts.Enabled
                        ]
                    NextCursor = Some "history-next"
                }

        let rows =
            document.Screen.Regions
            |> List.collect (fun region ->
                match region.Content with
                | RegionContent.Timeline entries -> entries
                | _ -> [])

        Expect.equal (rows |> List.map _.Key) [ "1/0"; "1/1" ] "Actual effect positions"

        Expect.equal
            (rows |> List.map _.Link)
            [ Some(Link.caseById parent); None ]
            "Only a scoped effect has a destination"

        Expect.equal document.NextCursor (Some "history-next") "Cursor belongs to the document"

        Expect.isOk
            (Codec.encode HumanDocumentCodecs.document document
             |> Codec.decode 65536 HumanDocumentCodecs.document)
            "Actual native boundary")
