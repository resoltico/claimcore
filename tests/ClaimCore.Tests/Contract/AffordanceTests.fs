module ClaimCore.Tests.AffordanceTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.TestSupport

let private parent =
    CaseId.create (Guid.Parse "10000000-0000-4000-8000-000000000001") |> Option.get

let private action () =
    TaskAffordances.caseAction CaseActKind.Close parent Revision.first Map.empty [] Map.empty

let private row key actions =
    {
        RegionRow.Key = key
        Values = []
        Detail = None
        Link = None
        Actions = actions
    }

let private region rows =
    {
        Region.Role = RegionRole.Results
        TitleId = None
        Content =
            RegionContent.Table
                {
                    Columns = []
                    Rows = rows
                    Groups = []
                    Sections = []
                    Meter = None
                }
        Link = None
        Actions = []
    }

[<Tests>]
let tests =
    testList
        "affordance construction"
        [
            testCase
                "bindings and scalar or collection seed keys remain declaration-owned"
                (fun () ->
                    let value = action ()

                    Expect.throws
                        (fun () ->
                            Affordance.create
                                value.Task
                                (OperationBinding.People None)
                                Map.empty
                                []
                                Map.empty
                                false
                                None
                            |> ignore)
                        "Another subject cannot bind the task"

                    Expect.throws
                        (fun () ->
                            Affordance.create
                                value.Task
                                value.Binding
                                (Map.ofList
                                    [
                                        TaskInputId.Target InputTarget.NoteText,
                                        SubmissionValue.Text "foreign"
                                    ])
                                []
                                Map.empty
                                false
                                None
                            |> ignore)
                        "A foreign scalar seed refuses"

                    let seed =
                        Map.ofList
                            [
                                TaskInputId.Target InputTarget.Grants,
                                SubmissionValue.Rows
                                    [
                                        Map.ofList
                                            [
                                                TaskInputId.Target InputTarget.NoteText,
                                                SubmissionValue.Text "foreign"
                                            ]
                                    ]
                            ]

                    Expect.throws
                        (fun () ->
                            Affordance.create
                                (TaskId.Operation(ActKind.People PeopleActKind.Admit))
                                (OperationBinding.People None)
                                seed
                                []
                                Map.empty
                                false
                                None
                            |> ignore)
                        "A foreign collection row seed refuses")
            testCase
                "duplicate rows and competing regional or document primaries refuse at the producer"
                (fun () ->
                    let primary = { action () with Primary = true }

                    Expect.throws
                        (fun () ->
                            Screen.create [ region [ row "same" []; row "same" [] ] ] |> ignore)
                        "Duplicate rendering keys refuse"

                    Expect.throws
                        (fun () ->
                            Screen.create [ region [ row "a" [ primary ]; row "b" [ primary ] ] ]
                            |> ignore)
                        "A region cannot recommend two actions"

                    let screen = Screen.create []

                    Expect.throws
                        (fun () ->
                            HumanDocument.create
                                DocumentSubject.List
                                screen
                                [ primary; primary ]
                                []
                                None
                                ObservationBasis.Unavailable
                                HumanDisclosure.Current
                                None
                            |> ignore)
                        "A document cannot recommend two actions")
            testCase "erasure and withheld disclosure cannot retain authoring seeds" (fun () ->
                for disclosure in [ HumanDisclosure.Erased; HumanDisclosure.Withheld ] do
                    Expect.throws
                        (fun () ->
                            HumanDocument.create
                                DocumentSubject.List
                                (Screen.create [])
                                [ action () ]
                                []
                                None
                                ObservationBasis.Unavailable
                                disclosure
                                None
                            |> ignore)
                        "Disclosure applies to controls as well as visible facts")
        ]
