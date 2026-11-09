module ClaimCore.Tests.PostgresBaselineTests

open Expecto
open ClaimCore.TestSupport

let private baseline =
    """{"supportedMajors":[{"major":18,"minimumMinor":6,"containerImage":"postgres:18.6@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},{"major":23,"minimumMinor":2,"containerImage":"postgres:23.2@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}]}"""

let private invalidBaselines () =
    for text in
        [
            """{"supportedMajors":[]}"""
            baseline.Replace("\"major\":23", "\"major\":18")
            baseline.Replace("postgres:23.2", "postgres:24.2")
            baseline.Replace("postgres:18.6", "postgres:18.5")
            baseline.Replace("@sha256:", "@sha512:")
        ] do
        Expect.throws (fun () -> PostgresBaseline.parse text |> ignore) "Invalid baseline refuses"

[<Tests>]
let tests =
    testList
        "PostgreSQL fixture baseline"
        [
            testCase "empty, duplicate, mismatched and unpinned baselines refuse" invalidBaselines
        ]
