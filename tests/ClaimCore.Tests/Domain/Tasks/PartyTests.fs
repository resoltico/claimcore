module ClaimCore.Tests.Tasks.PartyTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Tasks
open ClaimCore.Tests.TaskBuilders

let private partyTask name =
    match Registry.find (TaskId.ofLiteral name) with
    | Some(Task.Family value) -> value
    | _ -> failtest "Party task required."

let private row role name details =
    [
        text InputTarget.PartyRole role
        yield! name |> Option.map (text InputTarget.PartyName) |> Option.toList
        yield! details |> Option.map (text InputTarget.PartyDetails) |> Option.toList
    ]
    |> Map.ofList

let private opened rows =
    let submitted =
        InputMap.create
            (Map.ofList [ text (InputTarget.Field BusinessField.CaseReference) "PARTY-EXAMPLE" ])
            (Map.ofList [ TaskCollectionId.Parties, rows ])
        |> Option.get

    openCustom (context 23) environment None (Binding.CaseNew Revision.zero) submitted

let private named role name = row role (Some name) None

let private opening () =
    let changes =
        opened
            [
                named "CLAIMANT" "Anna Liepa"
                named "INSURER" "Example Insurance AS"
                named "CLAIMANT" "Anna Liepa"
            ]
        |> accepted

    Expect.equal
        (changes |> List.tail |> List.map (fun change -> change.PartId.ToString("D")))
        [
            "068c9086-1ef2-8716-98f5-78f9491a5be4"
            "861a3e29-d8b5-8e2e-8ec2-8c9e424696f9"
            "80d677c9-399a-82a5-8b8e-e2cc73667b17"
        ]
        "UUIDv8 vectors use the authored row order and distinct IDs even for duplicate names"

    let state =
        changes
        |> List.fold (fun state change -> Some(CaseFamily.apply state change |> accepted)) None
        |> Option.get

    let claimants =
        DecisionFacts.fromReplay state
        |> DecisionFacts.currentParties
        |> PartyContent.names PartyRole.Claimant
        |> List.map PartyName.format

    Expect.equal claimants [ "Anna Liepa"; "Anna Liepa" ] "Both current parties remain"

let private add number environment state values =
    let task = partyTask "party.add"
    let binding = Binding.PartyNew(target state)
    task.Decide (context number) environment (Some(DecisionFacts.fromReplay state)) binding values

let private boundaries () =
    let openCase = family ()

    let details =
        inputs
            [
                text InputTarget.PartyRole "OTHER"
                text InputTarget.PartyDetails "+371 20000000\nRiga"
            ]

    let added = add 2 environment openCase details |> accepted

    match added.Head.Value with
    | PartValue.Party party ->
        Expect.equal
            (party.Content |> Option.bind _.Details |> Option.map PartyDetails.format)
            (Some "+371 20000000\nRiga")
            "Details alone preserve LF"
    | _ -> failtest "Party value required."

    let empty = inputs [ text InputTarget.PartyRole "OTHER" ]
    let rejected = add 2 environment openCase empty |> expectInputs

    Expect.contains
        (rejected |> List.map _.Violation)
        Violation.PartyContentRequired
        "Empty content is refused"

    let badDetails =
        inputs
            [
                text InputTarget.PartyRole "OTHER"
                text InputTarget.PartyDetails "First\rSecond"
            ]

    let rejected = add 2 environment openCase badDetails |> expectInputs

    Expect.contains
        (rejected |> List.map _.Violation)
        Violation.ControlCharacters
        "Only LF is admitted"

    let state =
        [ 2..21 ]
        |> List.fold
            (fun state number -> add number environment state details |> accepted |> apply state)
            openCase

    add 22 environment state details
    |> expectRefusal (Refusal.Case CaseRefusal.PartyLimit)

    for lifecycle, headline in
        [ Lifecycle.Voided, CaseHeadline.Voided; Lifecycle.Erased, CaseHeadline.Erased ] do
        let inactive = setCase lifecycle CaseStatus.Opened openCase

        add 3 environment inactive details
        |> expectRefusal (Refusal.Case(CaseRefusal.NotOffered headline))

    let closed = setCase Lifecycle.Active CaseStatus.Closed openCase
    add 3 environment closed details |> accepted |> ignore

    let reader =
        AccessView.ofActor (
            Some
                {
                    Actor = actor
                    Grants =
                        [
                            {
                                Role = Role.CaseReader
                                Scope = Scope.Installation
                            }
                        ]
                }
        )

    add 3 { environment with Requester = reader } openCase details
    |> expectRefusal (Refusal.Access(AccessRefusal.NotAuthorized Capability.Register))

[<Tests>]
let tests =
    testList
        "case party ownership"
        [
            testCase
                "case opening creates a UUIDv8 per ordered row and keeps duplicate names"
                opening
            testCase
                "current party admission enforces content, count, access and lifecycle"
                boundaries
        ]
