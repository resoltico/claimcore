module ClaimCore.Tests.AssigneeDocumentTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.Wire
open ClaimCore.TestSupport

let private actor number =
    ActorId.create (Guid.Parse(sprintf "00000000-0000-4000-8000-%012d" number))
    |> Option.get

let private parent =
    CaseId.create (Guid.Parse "10000000-0000-4000-8000-000000000001") |> Option.get

let private document () =
    HumanReadDocuments.assignees
        {
            CaseId = parent
            WorkRevision = Revision.first
            Basis = Inputs.workBasis
            Items = [ Inputs.actorReference (actor 1); Inputs.actorReference (actor 2) ]
            NextCursor = Some "choice-next"
        }

let private rows document =
    document.Screen.Regions
    |> List.collect (fun region ->
        match region.Content with
        | RegionContent.Table table -> table.Rows
        | _ -> [])

[<Tests>]
let tests =
    testList
        "core assignee choice documents"
        [
            testCase
                "each choice carries its exact value and work binding independently of captions"
                (fun () ->
                    let original = document ()

                    let selections =
                        rows original |> List.map (fun row -> row.Actions |> List.exactlyOne)

                    Expect.equal
                        (selections
                         |> List.map (fun action ->
                             Map.find
                                 (TaskAffordances.field BusinessField.ResponsibleActor)
                                 action.Seeds))
                        [
                            SubmissionValue.Text(ActorId.format (actor 1))
                            SubmissionValue.Text(ActorId.format (actor 2))
                        ]
                        "No row-key join supplies the choice identity"

                    for action in selections do
                        Expect.equal
                            action.Binding
                            (OperationBinding.Work(CaseSelector.Id parent, 1L))
                            "The actual read's version binds the selection"

                    Expect.equal
                        original.NextCursor
                        (Some "choice-next")
                        "Paging is document metadata"

                    let opaque =
                        { original with
                            Screen =
                                { original.Screen with
                                    Regions =
                                        original.Screen.Regions
                                        |> List.map (fun region ->
                                            match region.Content with
                                            | RegionContent.Table table ->
                                                { region with
                                                    Content =
                                                        RegionContent.Table
                                                            { table with
                                                                Rows =
                                                                    table.Rows
                                                                    |> List.map (fun row ->
                                                                        { row with
                                                                            Values =
                                                                                [
                                                                                    PresentedValue
                                                                                        .Text(
                                                                                            "opaque:"
                                                                                            + row
                                                                                                .Key
                                                                                        )
                                                                                ]
                                                                        })
                                                            }
                                                }
                                            | _ -> region)
                                }
                        }

                    let encoded = Codec.encode HumanDocumentCodecs.document opaque

                    Expect.isOk
                        (Codec.decode 65536 HumanDocumentCodecs.document encoded)
                        "Captions are authoritative presentation"

                    Expect.stringContains
                        (Encoding.UTF8.GetString encoded)
                        "opaque:"
                        "Opaque wording survives"

                    Expect.equal
                        (rows opaque |> List.map _.Actions)
                        (rows original |> List.map _.Actions)
                        "Changing captions cannot change selection")
            testCase "an obsolete parallel handle member is rejected" (fun () ->
                let encoded =
                    Codec.encode HumanDocumentCodecs.document (document ())
                    |> Encoding.UTF8.GetString

                let altered =
                    encoded.Insert(1, "\"context\":{\"kind\":\"ASSIGNEES\",\"actorIds\":[]},")
                    |> Encoding.UTF8.GetBytes

                Expect.isError
                    (Codec.decode 65536 HumanDocumentCodecs.document altered)
                    "Retired controls cannot become a second source of identity")
        ]
