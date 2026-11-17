module ClaimCore.Tests.AffordanceTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.TestSupport
open ClaimCore.Wire
open ClaimCore.Json

let private parent =
    CaseId.create (Guid.Parse "10000000-0000-4000-8000-000000000001") |> Option.get

let private action () =
    TaskAffordances.caseAction
        (TaskId.ofLiteral "case.close")
        parent
        Revision.first
        Sequence.zero
        Map.empty
        []
        Map.empty

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
            testCase "bindings and scalar seed keys remain declaration-owned" (fun () ->
                let value = action ()

                Expect.throws
                    (fun () ->
                        Affordance.create
                            value.Task
                            (Some(Binding.PeopleNew Revision.zero))
                            Map.empty
                            []
                            Map.empty
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
                                    InputValue.Text "foreign"
                                ])
                            []
                            Map.empty
                            None
                        |> ignore)
                    "A foreign scalar seed refuses"

                let seed =
                    Map.ofList [ TaskInputId.Target InputTarget.Grants, InputValue.Text "foreign" ]

                Expect.throws
                    (fun () ->
                        Affordance.create
                            (TaskId.ofLiteral "people.admit")
                            (Some(Binding.PeopleNew Revision.zero))
                            seed
                            []
                            Map.empty
                            None
                        |> ignore)
                    "Collection identifiers are not scalar seed controls")
            testCase "scalar seed types and choice tokens refuse malformed controls" (fun () ->
                let control = TaskAffordances.work parent Revision.first Map.empty []

                for seed in [ InputValue.Integer 1L; InputValue.Boolean true ] do
                    let malformed =
                        { control with
                            Seeds =
                                Map.ofList
                                    [ TaskAffordances.field BusinessField.HandlingSummary, seed ]
                        }

                    Expect.isFalse
                        (Affordance.valid malformed)
                        "Text seeds retain their declared atom"

                let role =
                    { action () with
                        Task = TaskId.ofLiteral "people.grant"
                        Control =
                            ActionControl.Operation(
                                Binding.People(
                                    ActorId.create (
                                        Guid.Parse "10000000-0000-4000-8000-000000000002"
                                    )
                                    |> Option.get,
                                    Revision.first
                                )
                            )
                        Seeds =
                            Map.ofList
                                [ TaskInputId.Target InputTarget.Role, InputValue.Text "UNKNOWN" ]
                    }

                Expect.isFalse (Affordance.valid role) "Closed choice seeds keep their vocabulary"
                let encoded = PresentationCodecs.affordance.Write(action ())

                let malformed =
                    match encoded with
                    | JsonValue.Object fields ->
                        JsonValue.Object(
                            fields
                            |> List.map (fun (key, value) ->
                                if key = "seeds" then
                                    key, JsonValue.Object [ "field:closedOn", JsonValue.Array [] ]
                                else
                                    key, value)
                        )
                    | _ -> failtest "An affordance must be an object"

                Expect.isError
                    (PresentationCodecs.affordance.Read "$" malformed)
                    "Collection values are not scalar seeds")
            testCase "duplicate rows and undeclared document or regional controls refuse" (fun () ->
                Expect.throws
                    (fun () -> Screen.create [ region [ row "same" []; row "same" [] ] ] |> ignore)
                    "Duplicate rendering keys refuse"

                let valid = action ()

                let invalid =
                    { valid with
                        Seeds =
                            Map.ofList
                                [
                                    TaskInputId.Target InputTarget.NoteText,
                                    InputValue.Text "foreign"
                                ]
                    }

                Expect.throws
                    (fun () -> Screen.create [ region [ row "a" [ invalid ] ] ] |> ignore)
                    "A regional control cannot bypass its declaration"

                Expect.throws
                    (fun () ->
                        HumanDocument.create
                            DocumentSubject.List
                            (Screen.create [])
                            [ invalid ]
                            []
                            None
                            ObservationBasis.Unavailable
                            HumanDisclosure.Current
                            None
                        |> ignore)
                    "A document control cannot bypass its declaration"

                let other =
                    TaskAffordances.caseAction
                        (TaskId.ofLiteral "case.reopen")
                        parent
                        Revision.first
                        Sequence.zero
                        Map.empty
                        []
                        Map.empty

                let document =
                    HumanDocument.create
                        DocumentSubject.List
                        (Screen.create [])
                        [ valid; other ]
                        []
                        None
                        ObservationBasis.Unavailable
                        HumanDisclosure.Current
                        None

                Expect.equal
                    document.Actions.Length
                    2
                    "Generic action lists carry no competing primary flags")
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
