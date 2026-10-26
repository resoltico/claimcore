module ClaimCore.Tests.PostgresBaselineTests

open Expecto
open ClaimCore.TestSupport

let private baseline =
    """{"supportedMajors":[{"major":18,"minimumMinor":6,"containerImage":"postgres:18.6@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},{"major":23,"minimumMinor":2,"containerImage":"postgres:23.2@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}]}"""

let private selection () =
    let entries = PostgresBaseline.parse baseline

    Expect.equal
        (PostgresBaseline.select entries None).Major
        18
        "Local default is first listed major"

    let selected = PostgresBaseline.select entries (Some "23")
    Expect.equal selected.Major 23 "Every listed major can be selected"
    Expect.stringStarts selected.ContainerImage "postgres:23.2@" "Selected image follows major"

    for requested in [ "19"; ""; "018"; "18.6"; "postgres:18.6"; " 18" ] do
        Expect.throws
            (fun () -> PostgresBaseline.select entries (Some requested) |> ignore)
            "Unlisted or malformed selection must refuse before resource creation"

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
        "PostgreSQL fixture selection"
        [
            testCase "default and explicit listed major select matching pinned image" selection
            testCase "empty, duplicate, mismatched and unpinned baselines refuse" invalidBaselines
        ]
