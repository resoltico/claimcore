module ClaimCore.Tests.WorkDescriptorTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol.Presentation
open ClaimCore.Cli

let private basis =
    {
        ObservedAt = DateTimeOffset.Parse "2026-11-04T12:00:00Z"
        BusinessDate = DateOnly(2026, 11, 4)
        TimeZoneId = "Europe/Riga"
    }

[<Tests>]
let tests =
    testList
        "typed work descriptors"
        [
            testCase "overview preserves independent counts and a known zero" (fun () ->
                let reply =
                    {
                        Basis = basis
                        Mine = false
                        ActionableCases = 11L
                        MissingPlanCases = 0L
                        OverdueCases = 3L
                        OverdueReminders = 4L
                        UnassignedCases = 5L
                        UnavailableAssigneeCases = 6L
                        AttentionCases = 7L
                    }

                let entries = Descriptor.workOverview reply

                Expect.equal
                    (entries |> List.map (fun entry -> entry.Key, entry.Value))
                    [
                        "actionableCases", PresentedValue.Count 11L
                        "missingPlanCases", PresentedValue.Count 0L
                        "overdueCases", PresentedValue.Count 3L
                        "overdueReminders", PresentedValue.Count 4L
                        "unassignedCases", PresentedValue.Count 5L
                        "unavailableAssigneeCases", PresentedValue.Count 6L
                        "attentionCases", PresentedValue.Count 7L
                    ]
                    "The core determines count identity, order and value without client recomputation"

                Expect.contains
                    (TextWorkReply.overview "en" reply)
                    "Cases without next action: 0"
                    "The actual human renderer retains zero rather than an absent marker")
            testCase "view counts retain their typed identities and producer order" (fun () ->
                let cases: CaseViews =
                    {
                        Basis = basis
                        Items =
                            [
                                { View = CaseView.Mine; Count = 0L }
                                { View = CaseView.All; Count = 9L }
                            ]
                    }

                let reminders: ReminderViews =
                    {
                        Basis = basis
                        Items =
                            [
                                {
                                    View = ReminderView.Cancelled
                                    Count = 2L
                                }
                            ]
                    }

                Expect.equal
                    (Descriptor.caseViews cases
                     |> List.map (fun entry -> entry.LabelId, entry.Value))
                    [
                        "view.MINE.label", PresentedValue.Count 0L
                        "view.ALL.label", PresentedValue.Count 9L
                    ]
                    "Case view labels and counts come from typed core values"

                Expect.equal
                    (Descriptor.reminderViews reminders
                     |> List.map (fun entry -> entry.LabelId, entry.Value))
                    [ "view.CANCELLED.label", PresentedValue.Count 2L ]
                    "Reminder views do not borrow case selection rules")
        ]
