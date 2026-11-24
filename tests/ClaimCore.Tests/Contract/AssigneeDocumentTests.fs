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
            Basis = DomainInputs.workBasis
            Items = [ DomainInputs.actorReference (actor 1); DomainInputs.actorReference (actor 2) ]
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
        "core lookup and selection declarations"
        [
            testCase "core declares both lookups and every payee uses the party lookup" (fun () ->
                let payee = TaskInputId.Target InputTarget.PaymentPayee
                let responsible = TaskAffordances.field BusinessField.ResponsibleActor

                Expect.equal
                    TaskInputDeclaration.lookups
                    (Map.ofList [ responsible, "case.assignees"; payee, "case.parties" ])
                    "The input owns its declared query"

                let payeeInputs =
                    ClaimCore.Domain.Tasks.Registry.declarations
                    |> List.collect (fun task ->
                        task.Inputs
                        |> List.filter (fun input -> input.Input = payee)
                        |> List.map (fun input -> TaskId.token task.Id, input.ChoicesFrom))
                    |> Map.ofList

                Expect.equal
                    payeeInputs
                    (Map.ofList
                        [
                            "case.recordPayment", Some "case.parties"
                            "case.correctPayment", Some "case.parties"
                        ])
                    "Record and correction both use the same lookup")
            testCase
                "selection prompts belong to exactly the case tasks with a binding identity"
                (fun () ->
                    for task in ClaimCore.Domain.Tasks.Registry.declarations do
                        let binding = SubmissionDeclarations.binding task

                        Expect.equal
                            task.SelectionPromptId.IsSome
                            (binding.Subject = ActSubject.Case && binding.Identity.IsSome)
                            (TaskId.token task.Id))
            testCase
                "each choice carries its exact value and work binding independently of captions"
                (fun () ->
                    let original = document ()

                    Expect.equal
                        (rows original |> List.map _.Choice)
                        [ Some(ActorId.format (actor 1)); Some(ActorId.format (actor 2)) ]
                        "Choice values are explicit in the results document"

                    let selections =
                        rows original |> List.map (fun row -> row.Actions |> List.exactlyOne)

                    Expect.equal
                        (selections
                         |> List.map (fun action ->
                             Map.find
                                 (TaskAffordances.field BusinessField.ResponsibleActor)
                                 action.Seeds))
                        [
                            InputValue.Text(ActorId.format (actor 1))
                            InputValue.Text(ActorId.format (actor 2))
                        ]
                        "No row-key join supplies the choice identity"

                    for action in selections do
                        Expect.equal
                            action.Binding
                            (Some(Binding.Work(CaseSelector.Id parent, Revision.first)))
                            "The actual read's version binds the selection"

                    Expect.equal
                        original.NextCursor
                        (Some "choice-next")
                        "Paging is document metadata"

                    let opaque =
                        { original with
                            Screen =
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
                                                                            PresentedValue.Text(
                                                                                "opaque:"
                                                                                + row.Key
                                                                            )
                                                                        ]
                                                                })
                                                    }
                                        }
                                    | _ -> region)
                                |> fun regions -> Screen.withRegions regions original.Screen
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
