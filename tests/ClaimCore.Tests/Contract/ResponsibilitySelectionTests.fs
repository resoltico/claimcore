module ClaimCore.Tests.ResponsibilitySelectionTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Cli
open ClaimCore.Json
open ClaimCore.TestSupport

let private actorId =
    ActorId.create (Guid.Parse "b26d20d5-47c5-4522-9c8f-0982c5e08fd8") |> Option.get

[<Tests>]
let tests =
    testList
        "responsibility selection contracts"
        [
            testCase "responsibility survives admission and binds unreleased cursor six" (fun () ->
                let input =
                    { CaseListFilter.empty<string> with
                        ResponsibleActor = Some actorId
                        View = Some CaseView.All
                        Statuses = [ CaseStatus.Closed ]
                    }

                let filter = CaseListFilter.admit input |> Fixtures.accepted

                Expect.equal
                    filter.ResponsibleActor
                    (Some actorId)
                    "Admission preserves the typed actor filter"

                Expect.equal
                    filter.Statuses
                    [ CaseStatus.Closed ]
                    "Explicit filters remain intersections"

                let installation = Guid.Parse "81f5fe15-4789-49ac-b7ae-1f9e3f9688fe"
                let position = CaseListPosition.Reference "RESP-A"
                let cursor = Cursors.encodeCases installation filter position

                Expect.equal
                    (Cursors.decodeCases installation filter cursor)
                    (Ok position)
                    "The same selector continues"

                Expect.equal
                    (Cursors.decodeCases
                        installation
                        { filter with ResponsibleActor = None }
                        cursor)
                    (Error Refusal.InvalidCursor)
                    "Removing the actor changes the selection"

                let raw =
                    cursor.Replace('-', '+').Replace('_', '/')
                    + String('=', (4 - cursor.Length % 4) % 4)
                    |> Convert.FromBase64String
                    |> StrictJson.read 8192
                    |> Fixtures.accepted

                Expect.equal
                    (JsonValue.tryMember "v" raw)
                    (Some(JsonValue.Integer 6L))
                    "Unreleased format six is retained")
            testCase "case list and summary flags use the executable typed selection" (fun () ->
                for verb in [ "list"; "summary" ] do
                    let prepared =
                        Grammar.prepare
                            (fun () -> "unused")
                            [
                                "case"
                                verb
                                "--responsible-actor"
                                ActorId.format actorId
                                "--view"
                                "all"
                                "--dry-run"
                            ]
                        |> Fixtures.accepted

                    let frame = Frames.admit prepared.Bytes |> Fixtures.accepted

                    let filter =
                        if verb = "list" then
                            (Frames.value Api.caseList frame |> Option.get).Filter
                        else
                            Frames.value Api.caseSummary frame |> Option.get

                    Expect.equal
                        filter.ResponsibleActor
                        (Some actorId)
                        "The public flag reaches the same core filter"

                    Expect.equal
                        filter.View
                        (Some CaseView.All)
                        "The returned link does not substitute MINE")
            testCase "unknown is omitted while a known zero has an explicit ALL link" (fun () ->
                let actor =
                    {
                        ActorId = actorId
                        Principal =
                            PrincipalKey.human "https://issuer.example/" "target"
                            |> Fixtures.accepted
                        Enabled = true
                        Profile =
                            Some
                                {
                                    DisplayName = Inputs.name "Target"
                                    Contact = None
                                }
                    }

                let unknown = ActorReviewItem.ofActor [] actor

                Expect.isNone
                    (ResponseCodecs.actorSummary.Write unknown
                     |> JsonValue.tryMember "responsibleCases")
                    "No null or zero placeholder is emitted"

                let known =
                    { unknown with
                        ResponsibleCases =
                            Some
                                {
                                    Count = 0L
                                    ResponsibleActor = actorId
                                }
                    }

                let encoded = ResponseCodecs.actorSummary.Write known

                Expect.equal
                    (ResponseCodecs.actorSummary.Read "$" encoded)
                    (Ok known)
                    "A scoped zero round trips as known"

                let fields =
                    match JsonValue.tryMember "responsibleCases" encoded with
                    | Some(JsonValue.Object fields) -> fields
                    | _ -> failtest "Known responsibility object"

                Expect.equal
                    (List.tryFind (fst >> (=) "view") fields |> Option.map snd)
                    (Some(JsonValue.String "ALL"))
                    "The link's view is an invariant"

                let wrong =
                    JsonValue.Object(
                        fields
                        |> List.map (fun (name, value) ->
                            name, if name = "view" then JsonValue.String "MINE" else value)
                    )

                let forged =
                    match encoded with
                    | JsonValue.Object fields ->
                        JsonValue.Object(
                            fields
                            |> List.map (fun (name, value) ->
                                name, if name = "responsibleCases" then wrong else value)
                        )
                    | _ -> failtest "Actor object"

                Expect.isError
                    (ResponseCodecs.actorSummary.Read "$" forged)
                    "The link cannot silently narrow the counted selection")
        ]
