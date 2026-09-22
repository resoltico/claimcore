module ClaimCore.Tests.InstallationCalendarTests

open Expecto
open ClaimCore.Postgres

// Validation must stop bad zone identifiers before the unreachable database is opened.
let private unreachable =
    "Host=invalid.invalid;Username=claimcore_owner;Database=claimcore;Timeout=1"

let private refuses label zoneId =
    match InstallationBusinessZone.set unreachable zoneId with
    | AdministrationOutcome.NotStarted AdministrationFailure.BusinessZoneInvalid -> ()
    | result -> failtestf "Expected calendar admission refusal for %s, got %A" label result

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
                match InstallationBusinessZone.set unreachable "Etc/UTC" with
                | AdministrationOutcome.NotStarted AdministrationFailure.DatabaseUnavailable -> ()
                | result ->
                    failtestf "A canonical zone must reach database admission, got %A" result)
        ]
