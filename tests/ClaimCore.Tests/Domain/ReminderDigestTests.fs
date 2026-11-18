module ClaimCore.Tests.ReminderDigestTests

open System
open ClaimCore.Domain
open Expecto

let private emailBoundaries () =
    let accepts value =
        Expect.equal
            (EmailAddress.parse value |> Result.map EmailAddress.format)
            (Ok value)
            "Admitted addresses preserve spelling"

    accepts "example+digest@sample.test"
    accepts (String.replicate 64 "x" + "@sample.test")
    accepts ("x@" + String.replicate 250 "b" + ".c")

    for value in
        [
            "@sample.test"
            "a@@sample.test"
            "a@sample"
            "one two@sample.test"
            "a@sample.test\r\n"
            "a\u0000@sample.test"
            String.replicate 65 "x" + "@sample.test"
            "x@" + String.replicate 251 "b" + ".c"
        ] do
        Expect.equal
            (EmailAddress.parse value)
            (Error Violation.EmailAddressInvalid)
            "Reject invalid address shape"

let private counts () =
    let today = DateOnly(2026, 11, 18)

    let rows =
        [
            ReminderKind.Deadline, DateOnly(2026, 11, 17)
            ReminderKind.Deadline, DateOnly(2026, 11, 18)
            ReminderKind.Deadline, DateOnly(2026, 11, 19)
            ReminderKind.Deadline, DateOnly(2026, 11, 25)
            ReminderKind.Deadline, DateOnly(2026, 11, 26)
            ReminderKind.FollowUp, DateOnly(2026, 11, 10)
            ReminderKind.FollowUp, DateOnly(2026, 11, 18)
            ReminderKind.FollowUp, DateOnly(2026, 11, 19)
        ]

    Expect.equal
        (ReminderDigest.counts today rows)
        (Map.ofList
            [
                DigestCountKind.OverdueDeadlines, 1L
                DigestCountKind.DeadlinesDueToday, 1L
                DigestCountKind.UpcomingDeadlines, 2L
                DigestCountKind.OverdueFollowUps, 1L
                DigestCountKind.FollowUpsDueToday, 1L
            ])
        "The seventh day is included only for deadlines"

[<Tests>]
let tests =
    testList
        "daily reminder digest domain"
        [
            testCase
                "profile email admits bounded mailbox shapes without whitespace or controls"
                emailBoundaries
            testCase "the five configured digest categories use exact business-date windows" counts
        ]
