module ClaimCore.IntegrationTests.BusinessClockCaptureTests

open System
open System.Globalization
open Expecto
open ClaimCore.Hosting

type private CalendarClock(instant: DateTimeOffset) =
    inherit TimeProvider()
    let mutable calls = 0

    override _.GetUtcNow() =
        calls <- calls + 1
        instant.AddDays(float (calls - 1))

    member _.Calls = calls

let private calendarBoundaries () =
    let vectors =
        [
            "Europe/Riga", "2024-02-28T22:00:00Z", DateOnly(2024, 2, 29)
            "Europe/Riga", "2026-03-28T21:30:00Z", DateOnly(2026, 3, 28)
            "Europe/Riga", "2026-03-29T21:30:00Z", DateOnly(2026, 3, 30)
            "Europe/Riga", "2026-03-29T00:59:59Z", DateOnly(2026, 3, 29)
            "Europe/Riga", "2026-03-29T01:00:00Z", DateOnly(2026, 3, 29)
            "Europe/Riga", "2026-10-01T00:01:00Z", DateOnly(2026, 10, 1)
            "America/New_York", "2026-10-01T00:01:00Z", DateOnly(2026, 9, 30)
            "Pacific/Kiritimati", "2026-09-30T10:00:00Z", DateOnly(2026, 10, 1)
        ]

    for zone, value, expected in vectors do
        let instant = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture)
        let clock = CalendarClock(instant)
        let observed = (RuntimeOpening.businessTime zone clock).Capture()
        Expect.equal observed.EffectiveBusinessDate expected "Independent calendar boundary"

        Expect.equal
            observed.ObservedUtcInstant
            instant
            "Same captured instant accompanies the date"

        Expect.equal observed.TimeZoneId zone "Installed zone identity retained"
        Expect.equal clock.Calls 1 "A second capture would cross another business day"

let tests =
    testList
        "business calendar capture"
        [
            testCase
                "[CC-DOM-002] stored-zone capture pairs one instant across leap midnight and DST boundaries"
                calendarBoundaries
        ]
