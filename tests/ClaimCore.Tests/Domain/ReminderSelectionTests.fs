module ClaimCore.Tests.ReminderSelectionTests

open Expecto
open ClaimCore.Domain

[<Tests>]
let tests =
    testList
        "reminder query selection ownership"
        [
            testCase "omitted manual selection retains pending and active parents" (fun () ->
                let actual = ReminderSelection.resolve false None None

                Expect.equal
                    actual.State
                    (ReminderStateSelection.State ReminderState.Pending)
                    "Existing omitted-state contract"

                Expect.isFalse actual.IncludeInactive "Existing omitted-parent contract")
            testCase
                "a view defaults to no additional state constraint and includes inactive parents"
                (fun () ->
                    let actual = ReminderSelection.resolve true None None

                    Expect.equal
                        actual.State
                        ReminderStateSelection.Any
                        "The view owns its own state predicate"

                    Expect.isTrue actual.IncludeInactive "Existing view parent default")
            testCase "explicit any differs from the omitted pending default" (fun () ->
                let actual = ReminderSelection.resolve false (Some ReminderStateSelection.Any) None

                Expect.isNone
                    (ReminderStateSelection.state actual.State)
                    "Any supplies no state predicate"

                Expect.isFalse
                    actual.IncludeInactive
                    "Any does not change the independent parent filter")
            testCase "each explicit state survives either default basis" (fun () ->
                for state in
                    [ ReminderState.Pending; ReminderState.Completed; ReminderState.Cancelled ] do
                    for hasView in [ false; true ] do
                        let actual =
                            ReminderSelection.resolve
                                hasView
                                (Some(ReminderStateSelection.State state))
                                None

                        Expect.equal
                            (ReminderStateSelection.state actual.State)
                            (Some state)
                            "No state substitution")
            testCase "explicit false and true override either parent default" (fun () ->
                for hasView, value, expected in
                    [
                        false, false, false
                        false, true, true
                        true, false, false
                        true, true, true
                    ] do
                    let actual = ReminderSelection.resolve hasView None (Some value)

                    Expect.equal
                        actual.IncludeInactive
                        expected
                        "Absence alone permits the default")
            testCase "any is a query token and never a stored reminder state" (fun () ->
                Expect.equal
                    (Vocabulary.all ReminderStateSelection.vocabulary
                     |> List.map ReminderStateSelection.token)
                    [ "ANY"; "PENDING"; "COMPLETED"; "CANCELLED" ]
                    "Closed selector vocabulary"

                Expect.isNone
                    (Vocabulary.tryParse ReminderState.vocabulary "ANY")
                    "No invented lifecycle state")
        ]
