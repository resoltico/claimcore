module ClaimCore.Tests.ConsequenceTests

open ClaimCore.Application.Contracts
open Expecto
open ClaimCore.Domain
open ClaimCore.Cli
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json

let private acts =
    [
        (TaskId.ofLiteral "case.open"), Consequence.Routine
        (TaskId.ofLiteral "case.amendRegistration"), Consequence.Routine
        (TaskId.ofLiteral "case.changeReference"), Consequence.Routine
        (TaskId.ofLiteral "case.correct"), Consequence.Routine
        (TaskId.ofLiteral "case.decide"), Consequence.Routine
        (TaskId.ofLiteral "case.withdrawDecision"), Consequence.Routine
        (TaskId.ofLiteral "case.recordPayment"), Consequence.Routine
        (TaskId.ofLiteral "case.removePayment"), Consequence.Routine
        (TaskId.ofLiteral "case.correctPayment"), Consequence.Routine
        (TaskId.ofLiteral "case.close"), Consequence.Routine
        (TaskId.ofLiteral "case.reopen"), Consequence.Routine
        (TaskId.ofLiteral "case.void"), Consequence.Stewardship
        (TaskId.ofLiteral "case.reinstate"), Consequence.Stewardship
        (TaskId.ofLiteral "case.erase"), Consequence.Irreversible
        (TaskId.ofLiteral "people.provisionOwner"), Consequence.Routine
        (TaskId.ofLiteral "people.recoverOwner"), Consequence.Routine
        (TaskId.ofLiteral "people.register"), Consequence.Routine
        (TaskId.ofLiteral "people.admit"), Consequence.Routine
        (TaskId.ofLiteral "people.grant"), Consequence.Routine
        (TaskId.ofLiteral "people.revoke"), Consequence.Routine
        (TaskId.ofLiteral "people.enable"), Consequence.Routine
        (TaskId.ofLiteral "people.disable"), Consequence.Stewardship
        (TaskId.ofLiteral "people.setProfile"), Consequence.Routine
        (TaskId.ofLiteral "people.changeSignInIdentity"), Consequence.Routine
        (TaskId.ofLiteral "people.eraseProfile"), Consequence.Irreversible
        (TaskId.ofLiteral "party.add"), Consequence.Routine
        (TaskId.ofLiteral "party.update"), Consequence.Routine
        (TaskId.ofLiteral "party.remove"), Consequence.Routine
        (TaskId.ofLiteral "attachment.add"), Consequence.Stewardship
        (TaskId.ofLiteral "attachment.withdraw"), Consequence.Stewardship
        (TaskId.ofLiteral "note.add"), Consequence.Routine
        (TaskId.ofLiteral "note.remove"), Consequence.Routine
        (TaskId.ofLiteral "link.add"), Consequence.Routine
        (TaskId.ofLiteral "link.remove"), Consequence.Routine
        (TaskId.ofLiteral "work.update"), Consequence.Routine
        (TaskId.ofLiteral "reminder.create"), Consequence.Routine
        (TaskId.ofLiteral "reminder.update"), Consequence.Routine
        (TaskId.ofLiteral "reminder.complete"), Consequence.Routine
        (TaskId.ofLiteral "reminder.cancel"), Consequence.Routine
        (TaskId.ofLiteral "reminder.reopen"), Consequence.Routine
        (TaskId.ofLiteral "reminder.completeAndPlanNext"), Consequence.Routine
    ]

let private queries =
    [
        QueryTaskId.CaseGet, Consequence.Routine
        QueryTaskId.CaseAssignees, Consequence.Routine
        QueryTaskId.CaseList, Consequence.Routine
        QueryTaskId.CaseSummary, Consequence.Routine
        QueryTaskId.CaseTimeline, Consequence.Routine
        QueryTaskId.CaseAttachments, Consequence.Routine
        QueryTaskId.CaseNotes, Consequence.Routine
        QueryTaskId.CasePayments, Consequence.Routine
        QueryTaskId.ReminderList, Consequence.Routine
        QueryTaskId.ReminderGet, Consequence.Routine
        QueryTaskId.WorkViews, Consequence.Routine
        QueryTaskId.OperationGet, Consequence.Routine
        QueryTaskId.PeopleList, Consequence.Routine
        QueryTaskId.PeopleGrants, Consequence.Routine
        QueryTaskId.PeopleHistory, Consequence.Routine
        QueryTaskId.PeopleRequests, Consequence.Routine
        QueryTaskId.Context, Consequence.Routine
    ]

[<Tests>]
let tests =
    testList
        "core consequence catalog"
        [
            testCase "every declared act has an independent consequence pin" (fun () ->
                Expect.equal
                    (acts |> List.map fst |> List.distinct |> List.length)
                    acts.Length
                    "No duplicate pins"

                Expect.equal
                    (acts |> List.map fst |> Set.ofList)
                    (ClaimCore.Domain.Tasks.Registry.declarations |> List.map _.Id |> Set.ofList)
                    "Every declared act, including owner provisioning, requires a pin"

                for act, expected in acts do
                    Expect.equal
                        (TaskConsequences.ofTask act)
                        expected
                        "Classification follows the adopted consequence policy")
            testCase "every query has an explicit routine consequence" (fun () ->
                let declared =
                    ActorEndpoint.all
                    |> List.choose (function
                        | ActorEndpoint.Query query -> Some query
                        | ActorEndpoint.Operation _
                        | ActorEndpoint.Command _ -> None)
                    |> Set.ofList

                Expect.equal
                    (queries |> List.map fst |> Set.ofList)
                    declared
                    "No query silently inherits a future default"

                for query, expected in queries do
                    Expect.equal
                        (TaskRouting.consequence (QueryTaskId.key query))
                        expected
                        "SETTLE is the Resolve query, not a read-only consequence")
            testCase "the actual CLI consumes Core operation consequences for every task" (fun () ->
                for act, expected in acts do
                    let declaration =
                        ClaimCore.Domain.Tasks.Registry.findDeclaration act |> Option.get

                    if declaration.Exposure = TaskExposure.Client then
                        Expect.equal
                            (TaskRouting.consequence (TaskId.token act))
                            expected
                            "CLI confirmation cannot downgrade Core consequence")
            testCase "consequence wire vocabulary is closed and stable" (fun () ->
                Expect.equal
                    (Vocabulary.tokens Consequence.vocabulary)
                    [ "ROUTINE"; "STEWARDSHIP"; "IRREVERSIBLE" ]
                    "The presentation contract uses the adopted tokens")
        ]
