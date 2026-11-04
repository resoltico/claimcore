module ClaimCore.Tests.ConsequenceTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Json

let private acts =
    [
        ActKind.Case CaseActKind.Open, Consequence.Routine
        ActKind.Case CaseActKind.AmendRegistration, Consequence.Routine
        ActKind.Case CaseActKind.Correct, Consequence.Routine
        ActKind.Case CaseActKind.Decide, Consequence.Routine
        ActKind.Case CaseActKind.WithdrawDecision, Consequence.Routine
        ActKind.Case CaseActKind.RecordPayment, Consequence.Routine
        ActKind.Case CaseActKind.ClearPayment, Consequence.Routine
        ActKind.Case CaseActKind.Close, Consequence.Routine
        ActKind.Case CaseActKind.Reopen, Consequence.Routine
        ActKind.Case CaseActKind.Void, Consequence.Stewardship
        ActKind.Case CaseActKind.Reinstate, Consequence.Stewardship
        ActKind.Case CaseActKind.Erase, Consequence.Irreversible
        ActKind.Authority AuthorityKind.ProvisionOwner, Consequence.Routine
        ActKind.Authority AuthorityKind.Register, Consequence.Routine
        ActKind.Authority AuthorityKind.Grant, Consequence.Routine
        ActKind.Authority AuthorityKind.Revoke, Consequence.Routine
        ActKind.Authority AuthorityKind.Enable, Consequence.Routine
        ActKind.Authority AuthorityKind.Disable, Consequence.Stewardship
        ActKind.Authority AuthorityKind.SetProfile, Consequence.Routine
        ActKind.Authority AuthorityKind.EraseProfile, Consequence.Irreversible
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
        QueryEndpoint.CaseGet, Consequence.Routine
        QueryEndpoint.CaseAssignees, Consequence.Routine
        QueryEndpoint.CaseList, Consequence.Routine
        QueryEndpoint.CaseViews, Consequence.Routine
        QueryEndpoint.ReminderViews, Consequence.Routine
        QueryEndpoint.CaseSummary, Consequence.Routine
        QueryEndpoint.CaseHistory, Consequence.Routine
        QueryEndpoint.CaseActivity, Consequence.Routine
        QueryEndpoint.CaseTimeline, Consequence.Routine
        QueryEndpoint.ReminderList, Consequence.Routine
        QueryEndpoint.ReminderGet, Consequence.Routine
        QueryEndpoint.WorkOverview, Consequence.Routine
        QueryEndpoint.Observe, Consequence.Routine
        QueryEndpoint.Status, Consequence.Routine
        QueryEndpoint.Resolve, Consequence.Irreversible
        QueryEndpoint.AuthorityList, Consequence.Routine
        QueryEndpoint.AuthorityGrants, Consequence.Routine
        QueryEndpoint.AuthorityHistory, Consequence.Routine
        QueryEndpoint.AuthorityRequests, Consequence.Routine
        QueryEndpoint.AuthorityRequestsDismiss, Consequence.Routine
        QueryEndpoint.Context, Consequence.Routine
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
            testCase "every query is pinned and operation settlement is irreversible" (fun () ->
                let declared =
                    ActorEndpoint.all
                    |> List.choose (function
                        | ActorEndpoint.Query query -> Some query
                        | ActorEndpoint.Operation _ -> None)
                    |> Set.ofList

                Expect.equal
                    (queries |> List.map fst |> Set.ofList)
                    declared
                    "No query silently inherits a future default"

                for query, expected in queries do
                    Expect.equal
                        (Consequence.ofQuery query)
                        expected
                        "SETTLE is the Resolve query, not a read-only consequence")
            testCase "catalog entries emit the authoritative consequence and round trip" (fun () ->
                let expected =
                    (acts |> List.map (fun (act, value) -> CatalogOperation.Act act, value))
                    @ (queries
                       |> List.map (fun (query, value) -> CatalogOperation.Query query, value))
                    |> Map.ofList

                for entry in OperationCatalog.entries do
                    Expect.equal
                        entry.Consequence
                        (Map.find entry.Kind expected)
                        "The catalog does not infer consequence in clients"

                    let encoded = CatalogCodecs.entry.Write entry

                    Expect.equal
                        (JsonValue.tryMember "consequence" encoded)
                        (Some(
                            JsonValue.String(
                                Vocabulary.token Consequence.vocabulary entry.Consequence
                            )
                        ))
                        "The field is emitted on the wire"

                    Expect.equal
                        (CatalogCodecs.entry.Read "$" encoded)
                        (Ok entry)
                        "Typed classification survives the catalog codec")
            testCase
                "false missing and unknown consequences are rejected at catalog admission"
                (fun () ->
                    let erase =
                        OperationCatalog.entries
                        |> List.find (fun entry ->
                            entry.Kind = CatalogOperation.Act(ActKind.Case CaseActKind.Erase))

                    let fields =
                        match CatalogCodecs.entry.Write erase with
                        | JsonValue.Object fields -> fields
                        | _ -> failtest "Catalog entry object"

                    for value in [ "ROUTINE"; "STEWARDSHIP"; "UNKNOWN" ] do
                        let changed =
                            JsonValue.Object(
                                fields
                                |> List.map (fun (name, original) ->
                                    name,
                                    if name = "consequence" then
                                        JsonValue.String value
                                    else
                                        original)
                            )

                        Expect.isError
                            (CatalogCodecs.entry.Read "$" changed)
                            "A shape-valid downgrade cannot weaken irreversible metadata"

                    Expect.isError
                        (CatalogCodecs.entry.Read
                            "$"
                            (JsonValue.Object(
                                fields |> List.filter (fun (name, _) -> name <> "consequence")
                            )))
                        "The required field has no compatibility default")
            testCase "consequence wire vocabulary is closed and stable" (fun () ->
                Expect.equal
                    (Vocabulary.tokens Consequence.vocabulary)
                    [ "ROUTINE"; "STEWARDSHIP"; "IRREVERSIBLE" ]
                    "The presentation contract uses the adopted tokens")
        ]
