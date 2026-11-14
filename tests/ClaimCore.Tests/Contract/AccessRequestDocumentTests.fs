module ClaimCore.Tests.AccessRequestDocumentTests

open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport

let private principal =
    PrincipalKey.human "https://issuer.example/" "request-handle"
    |> Result.defaultWith (string >> failtest)

let private document name =
    HumanReadDocuments.requests
        {
            Basis = Inputs.workBasis
            NextCursor = Some "request-next"
            Items =
                [
                    {
                        Principal = principal
                        FirstSeen = Inputs.workBasis.ObservedAt
                        LastSeen = Inputs.workBasis.ObservedAt
                        Claims =
                            RequesterClaims.admit
                                name
                                (Some "fallback input hint")
                                (Some "contact@example.test")
                    }
                ]
        }

let private actions document =
    document.Screen.Regions
    |> List.collect (fun region ->
        match region.Content with
        | RegionContent.Table table -> table.Rows |> List.collect _.Actions
        | _ -> [])

[<Tests>]
let tests =
    testList
        "access request human controls"
        [
            testCase
                "principal seeds stay fixed while unverified profile hints remain editable"
                (fun () ->
                    let original = document (Some "preferred input hint")

                    let admit =
                        actions original
                        |> List.find (fun action ->
                            action.Task = TaskId.Operation(ActKind.People PeopleActKind.Admit))

                    Expect.equal
                        admit.Binding
                        (OperationBinding.People None)
                        "Admission selects an unregistered principal through authored inputs"

                    Expect.equal
                        (Map.find (TaskAffordances.input InputTarget.PrincipalSubject) admit.Seeds)
                        (SubmissionValue.Text "request-handle")
                        "Identity comes from the row's action"

                    Expect.equal
                        (Map.find
                            (TaskAffordances.input InputTarget.ProfileDisplayName)
                            admit.Seeds)
                        (SubmissionValue.Text "preferred input hint")
                        "Name hint precedes username"

                    Expect.equal
                        (Map.find (TaskAffordances.input InputTarget.ProfileContact) admit.Seeds)
                        (SubmissionValue.Text "contact@example.test")
                        "Contact remains an unverified input hint"

                    Expect.isFalse
                        (List.contains
                            (TaskAffordances.input InputTarget.PrincipalSubject)
                            admit.Editable)
                        "A request's principal cannot be edited into another target"

                    Expect.contains
                        admit.Editable
                        (TaskAffordances.input InputTarget.ProfileDisplayName)
                        "A human authors the profile"

                    let fallback =
                        actions (document None)
                        |> List.find (fun action -> action.Task = admit.Task)

                    Expect.equal
                        (Map.find
                            (TaskAffordances.input InputTarget.ProfileDisplayName)
                            fallback.Seeds)
                        (SubmissionValue.Text "fallback input hint")
                        "Missing names use the explicit hint"

                    Expect.equal
                        original.NextCursor
                        (Some "request-next")
                        "Paging stays independent of captions"

                    let bytes = Codec.encode HumanDocumentCodecs.document original

                    Expect.isOk
                        (Codec.decode 65536 HumanDocumentCodecs.document bytes)
                        "Actual row controls round trip")
            testCase "request rows carry their own dismissal command" (fun () ->
                let dismiss =
                    actions (document None)
                    |> List.find (fun action ->
                        action.Task = TaskId.Command CommandTaskId.PeopleRequestsDismiss)

                Expect.equal
                    dismiss.Seeds
                    (TaskAffordances.principal principal)
                    "The command targets the exact row principal"

                Expect.isEmpty dismiss.Editable "Dismissal cannot change the selected identity")
        ]
