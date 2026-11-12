module ClaimCore.Tests.NoteDocumentTests

open System
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport

let private id =
    NoteId.create (Guid.Parse "00000000-0000-4000-8000-000000000001") |> Option.get

let private notes =
    {
        CaseNotes.Items =
            [
                {
                    NoteId = id
                    Text =
                        NoteText.parse "Core note narrative Ω"
                        |> Result.defaultWith (string >> failtest)
                    RecordedAt = Inputs.workBasis.ObservedAt
                    RecordedBy = ResponseExamples.caseRead.RecordedBy
                    CanRemove = true
                }
            ]
        Count = 1L
        CanAdd = true
    }

let private documents () =
    [
        HumanReadDocuments.notes
            {
                Basis = Inputs.workBasis
                CaseId = ResponseExamples.caseRead.Snapshot.CaseId
                Reference = ResponseExamples.caseRead.Snapshot.Reference
                Notes = notes
                NextCursor = Some "notes-next"
            }
        HumanCaseDocument.ofCore
            { ResponseExamples.caseRead with
                Notes = notes
            }
    ]

let private nonNull value =
    value |> Option.ofObj |> Option.defaultWith (fun () -> failtest "Missing node")

[<Tests>]
let tests =
    testList
        "note human controls"
        [
            testCase
                "case card and paged notes bind typed removal identities to core display rows"
                (fun () ->
                    for value in documents () do
                        let handles, tasks =
                            match value.Context with
                            | InteractionContext.Case c -> c.Notes, c.Tasks
                            | InteractionContext.NotePage c -> c.Notes, c.Tasks
                            | _ -> failtest "Note controls required"

                        Expect.equal
                            handles
                            [ { NoteId = id; CanRemove = true } ]
                            "Only identity and control permission"

                        Expect.contains
                            tasks
                            (TaskId.Operation(ActKind.Note NoteActKind.RemoveNote))
                            "Core removal task"

                        Expect.isOk
                            (Codec.encode HumanDocumentCodecs.document value
                             |> Codec.decode 65536 HumanDocumentCodecs.document)
                            "Actual boundary"

                        let captions =
                            value.Screen.Regions
                            |> List.filter (fun region -> region.Role = RegionRole.Notes)

                        Expect.equal captions.Length 1 "Supplied Notes Screen retained")
            testCase
                "missing, duplicate, unoffered and tightened note controls are refused"
                (fun () ->
                    for value in documents () do
                        let bytes = Codec.encode HumanDocumentCodecs.document value

                        for mutate in
                            [
                                (fun (n: JsonNode) ->
                                    (nonNull n["context"])["notes"] <- JsonArray())
                                (fun n ->
                                    let c = nonNull n["context"] in
                                    let items = nonNull c["notes"] in
                                    (items.AsArray()).Add(nonNull items[0] |> _.DeepClone()))
                                (fun n -> (nonNull n["context"])["tasks"] <- JsonArray())
                                (fun n ->
                                    n["disclosure"] <-
                                        System.Text.Json.Nodes.JsonValue.Create "WITHHELD")
                            ] do
                            let node = JsonNode.Parse(Encoding.UTF8.GetString bytes) |> nonNull
                            mutate node

                            Expect.isError
                                (Encoding.UTF8.GetBytes(node.ToJsonString())
                                 |> Codec.decode 65536 HumanDocumentCodecs.document)
                                "Control binding")
        ]
