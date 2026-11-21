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

let private document name verified =
    HumanReadDocuments.requests
        {
            Basis = DomainInputs.workBasis
            NextCursor = Some "request-next"
            Items =
                [
                    {
                        Principal = principal
                        FirstSeen = DomainInputs.workBasis.ObservedAt
                        LastSeen = DomainInputs.workBasis.ObservedAt
                        Claims =
                            RequesterClaims.admit
                                name
                                (Some "fallback input hint")
                                (Some "contact@example.test")
                                verified
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
                    let original = document (Some "preferred input hint") false

                    let admit =
                        actions original
                        |> List.find (fun action ->
                            action.Task = (TaskId.ofLiteral "people.admit"))

                    Expect.equal
                        admit.Binding
                        (Some(Binding.PeopleNew Revision.zero))
                        "Admission selects an unregistered principal through authored inputs"

                    Expect.equal
                        (Map.find (TaskAffordances.input InputTarget.PrincipalSubject) admit.Seeds)
                        (InputValue.Text "request-handle")
                        "Identity comes from the row's action"

                    Expect.equal
                        (Map.find
                            (TaskAffordances.input InputTarget.ProfileDisplayName)
                            admit.Seeds)
                        (InputValue.Text "preferred input hint")
                        "Name hint precedes username"

                    Expect.equal
                        (Map.find (TaskAffordances.input InputTarget.ProfileContact) admit.Seeds)
                        (InputValue.Text "contact@example.test")
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
                        actions (document None false)
                        |> List.find (fun action -> action.Task = admit.Task)

                    Expect.equal
                        (Map.find
                            (TaskAffordances.input InputTarget.ProfileDisplayName)
                            fallback.Seeds)
                        (InputValue.Text "fallback input hint")
                        "Missing names use the explicit hint"

                    Expect.equal
                        original.NextCursor
                        (Some "request-next")
                        "Paging stays independent of captions"

                    let bytes = Codec.encode HumanDocumentCodecs.document original

                    Expect.isOk
                        (Codec.decode 65536 HumanDocumentCodecs.document bytes)
                        "Actual row controls round trip")
            testCase
                "issuer and subject identify the row and email verification is explicit"
                (fun () ->
                    let firstRow verified =
                        (document (Some "Display hint") verified).Screen.Regions
                        |> List.pick (fun region ->
                            match region.Content with
                            | RegionContent.Table table ->
                                Some(table.Columns, List.head table.Rows)
                            | _ -> None)

                    let columns, row = firstRow false
                    let fields = List.zip columns row.Values

                    for (key, expected) in
                        [ "issuer", "https://issuer.example/"; "subject", "request-handle" ] do
                        let column, value =
                            fields |> List.find (fun (column, _) -> column.Key = key)

                        Expect.equal column.Prominence Prominence.Primary "Identity is prominent"

                        Expect.equal
                            value
                            (PresentedValue.Identifier expected)
                            "Exact identity value"

                    let hintStatus field (fields: (Column * PresentedValue) list) =
                        fields |> List.find (fun (column, _) -> column.Key = field) |> snd

                    let unverified = PresentedValue.Message "label.unverifiedProviderHint"

                    Expect.equal
                        (hintStatus "profileHintStatus" fields)
                        unverified
                        "A display hint is unverified"

                    Expect.equal
                        (hintStatus "emailHintStatus" fields)
                        unverified
                        "A false email claim is unverified"

                    let verifiedColumns, verifiedRow = firstRow true

                    Expect.equal
                        (hintStatus "emailHintStatus" (List.zip verifiedColumns verifiedRow.Values))
                        (PresentedValue.Message "label.providerVerifiedEmail")
                        "Only the verified email changes its label")
            testCase "request rows carry their own dismissal command" (fun () ->
                let dismiss =
                    actions (document None false)
                    |> List.find (fun action ->
                        action.Task = (TaskId.ofLiteral "people.requests.dismiss"))

                Expect.equal
                    dismiss.Control
                    (ActionControl.Command(CommandRequest.DismissAccessRequest principal))
                    "The command targets the exact row principal"

                Expect.isEmpty dismiss.Editable "Dismissal cannot change the selected identity")
        ]
