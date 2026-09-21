module ClaimCore.Tests.InstallationCalendarTests

open System
open Expecto
open ClaimCore.Postgres

/// The installation calendar is validated before any connection is opened, so these refusals are
/// reachable without a database. They are the branches that stop a host-local default, a Windows
/// identifier, an alias, or an unknown identifier from becoming the calendar every business date is
/// derived from. The unreachable host keeps the test honest: only the calendar refusal names the
/// calendar, so a later failure cannot be mistaken for one.
let private unreachable = "Host=invalid.invalid"

let private refusedByCalendar (error: exn) =
    error.Message.Contains("canonical IANA zone", StringComparison.Ordinal)

let private refuses label zoneId =
    let error =
        Expect.throwsC (fun () -> InstallationBusinessZone.set unreachable zoneId) (fun error ->
            error)

    Expect.isTrue
        (refusedByCalendar error)
        ("A non-canonical business zone is refused before any connection: " + label)

let tests =
    testList
        "installation business calendar"
        [
            testCase
                "only a canonical zone identifier can become the installation calendar"
                (fun () ->
                    refuses "empty" ""
                    refuses "whitespace" "   "
                    refuses "leading whitespace" " Etc/UTC"
                    refuses "trailing whitespace" "Etc/UTC "
                    refuses "unknown zone" "Mars/Olympus"
                    refuses "abbreviation" "CET+1"
                    refuses "windows identifier" "W. Europe Standard Time"
                    refuses "offset literal" "+02:00"
                    refuses "empty segment" "Europe/"
                    refuses "traversal" "../Europe/Vilnius")

            testCase "a canonical zone passes validation and fails later, on the host" (fun () ->
                let error =
                    Expect.throwsC
                        (fun () -> InstallationBusinessZone.set unreachable "Etc/UTC")
                        (fun error -> error)

                Expect.isFalse
                    (refusedByCalendar error)
                    "A canonical zone is not refused by calendar validation")
        ]
