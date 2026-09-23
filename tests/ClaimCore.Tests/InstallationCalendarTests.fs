module ClaimCore.Tests.InstallationCalendarTests

open Expecto
open ClaimCore.Postgres

// Validation must stop bad zone identifiers before the unreachable database is opened.
let private unreachable =
    "Host=127.0.0.1;Port=1;Username=claimcore_owner;Database=claimcore;Timeout=1"

let private refuses label zoneId =
    match SchemaBaseline.initialize unreachable zoneId with
    | AdministrationOutcome.NotStarted AdministrationFailure.BusinessZoneInvalid -> ()
    | _ -> failtest ("Expected calendar admission refusal for " + label + ".")

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
                match SchemaBaseline.initialize unreachable "Etc/UTC" with
                | AdministrationOutcome.NotStarted AdministrationFailure.DatabaseUnavailable -> ()
                | _ -> failtest "A canonical zone must reach database admission.")
        ]
