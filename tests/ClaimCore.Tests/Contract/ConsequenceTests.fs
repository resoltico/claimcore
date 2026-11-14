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
        ActKind.Case CaseActKind.Open, Consequence.Routine
        ActKind.Case CaseActKind.AmendRegistration, Consequence.Routine
        ActKind.Case CaseActKind.Correct, Consequence.Routine
        ActKind.Case CaseActKind.Decide, Consequence.Routine
        ActKind.Case CaseActKind.WithdrawDecision, Consequence.Routine
        ActKind.Case CaseActKind.RecordPayment, Consequence.Routine
        ActKind.Case CaseActKind.RemovePayment, Consequence.Routine
        ActKind.Case CaseActKind.Close, Consequence.Routine
        ActKind.Case CaseActKind.Reopen, Consequence.Routine
        ActKind.Case CaseActKind.Void, Consequence.Stewardship
        ActKind.Case CaseActKind.Reinstate, Consequence.Stewardship
        ActKind.Case CaseActKind.Erase, Consequence.Irreversible
        ActKind.People PeopleActKind.ProvisionOwner, Consequence.Routine
        ActKind.People PeopleActKind.Register, Consequence.Routine
        ActKind.People PeopleActKind.Admit, Consequence.Routine
        ActKind.People PeopleActKind.Grant, Consequence.Routine
        ActKind.People PeopleActKind.Revoke, Consequence.Routine
        ActKind.People PeopleActKind.Enable, Consequence.Routine
        ActKind.People PeopleActKind.Disable, Consequence.Stewardship
        ActKind.People PeopleActKind.SetProfile, Consequence.Routine
        ActKind.People PeopleActKind.EraseProfile, Consequence.Irreversible
        ActKind.Note NoteActKind.AddNote, Consequence.Routine
        ActKind.Note NoteActKind.RemoveNote, Consequence.Routine
        ActKind.Work WorkActKind.UpdateWork, Consequence.Routine
        ActKind.Reminder ReminderActKind.Create, Consequence.Routine
        ActKind.Reminder ReminderActKind.Update, Consequence.Routine
        ActKind.Reminder ReminderActKind.Complete, Consequence.Routine
        ActKind.Reminder ReminderActKind.Cancel, Consequence.Routine
        ActKind.Reminder ReminderActKind.Reopen, Consequence.Routine
        ActKind.Reminder ReminderActKind.CompleteAndPlanNext, Consequence.Routine
    ]

let private queries =
    [
        QueryTaskId.CaseGet, Consequence.Routine
        QueryTaskId.CaseAssignees, Consequence.Routine
        QueryTaskId.CaseList, Consequence.Routine
        QueryTaskId.CaseSummary, Consequence.Routine
        QueryTaskId.CaseTimeline, Consequence.Routine
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
                    (Set.ofList ActKind.all)
                    "Every declared act, including owner provisioning, requires a pin"

                for act, expected in acts do
                    Expect.equal
                        (Consequence.ofAct act)
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
                        (TaskRouting.consequence (TaskId.Query query))
                        expected
                        "SETTLE is the Resolve query, not a read-only consequence")
            testCase "the actual CLI consumes Core operation consequences for every task" (fun () ->
                for act, expected in acts do
                    if act <> ActKind.People PeopleActKind.ProvisionOwner then
                        Expect.equal
                            (TaskRouting.consequence (TaskId.Operation act))
                            expected
                            "CLI confirmation cannot downgrade Core consequence")
            testCase "consequence wire vocabulary is closed and stable" (fun () ->
                Expect.equal
                    (Vocabulary.tokens Consequence.vocabulary)
                    [ "ROUTINE"; "STEWARDSHIP"; "IRREVERSIBLE" ]
                    "The presentation contract uses the adopted tokens")
        ]
