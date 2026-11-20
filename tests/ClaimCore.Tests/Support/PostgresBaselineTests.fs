module ClaimCore.Tests.PostgresBaselineTests

open System
open Expecto
open ClaimCore.TestSupport
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage

let private pinnedImage =
    "postgres:19.1@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

let private entry =
    """{"major":19,"minimumMinor":1,"containerImage":"postgres:19.1@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}"""

let private profile =
    {
        Encoding = "UTF8"
        Provider = "b"
        Locale = "C.UTF-8"
        Checksums = "on"
    }

let private validArgs =
    "--encoding=UTF8 --locale-provider=builtin --builtin-locale=C.UTF-8"

let private sample =
    "{\"supportedMajors\":["
    + entry
    + "],\"initdbArguments\":\""
    + validArgs
    + "\"}"

[<Tests>]
let tests =
    testList
        "PostgreSQL database profile"
        [
            testCase
                "the repository baseline supplies the embedded image and initdb arguments"
                (fun () ->
                    let image, args = PostgresBaseline.configuration ()
                    Expect.equal image ServerSupport.supported.ContainerImage "One pinned image"

                    Expect.equal
                        args
                        ServerSupport.supported.InitdbArguments
                        "One initdb argument list"

                    Expect.equal ServerSupport.supported.Major 19 "Only PostgreSQL 19 is supported"

                    Expect.equal
                        (PostgresBaseline.parse sample)
                        (pinnedImage, validArgs)
                        "A valid pin parses")
            testCase "missing, empty, multiple, mismatched and unpinned baselines refuse" (fun () ->
                for invalid in
                    [
                        """{"supportedMajors":[""" + entry + "]}"
                        sample.Replace(validArgs, " ")
                        sample.Replace(entry, "")
                        sample.Replace(entry, entry + "," + entry)
                        sample.Replace(entry, entry + "," + entry.Replace("19", "23"))
                        sample.Replace("postgres:19.1", "postgres:20.1")
                        sample.Replace("postgres:19.1", "postgres:19.0")
                        sample.Replace("@sha256:", "@sha512:")
                    ] do
                    Expect.throws (fun () -> PostgresBaseline.parse invalid |> ignore) invalid)
            testCase
                "the version decision accepts the pin and later minors in the same major"
                (fun () ->
                    let current = ServerSupport.supported
                    let decide = ServerSupport.versionFault current

                    Expect.isNone
                        (decide (System.Version(19, current.MinimumMinor)))
                        "Pinned 19 minor passes"

                    Expect.isNone
                        (decide (System.Version(19, current.MinimumMinor + 1)))
                        "Later 19 minor passes"

                    for version in [ System.Version(18, 7); System.Version(20, 0) ] do
                        Expect.equal
                            (decide version)
                            (Some OperatorFault.UnsupportedServer)
                            "Other majors refuse"

                    // The released 19.0 pin has no earlier minor. Exercise the floor with a synthetic 19.1 pin.
                    let future = { current with MinimumMinor = 1 }

                    Expect.equal
                        (ServerSupport.versionFault future (System.Version(19, 0)))
                        (Some OperatorFault.UnsupportedServer)
                        "A minor below the configured floor refuses")
            testCase "every database-profile condition is enforced independently" (fun () ->
                Expect.isNone (ServerSupport.profileFault profile) "The supported database passes"

                for invalid in
                    [
                        { profile with Encoding = "SQL_ASCII" }
                        { profile with Provider = "c" }
                        { profile with Provider = "i" }
                        { profile with Locale = "C" }
                        { profile with Checksums = "off" }
                    ] do
                    Expect.equal
                        (ServerSupport.profileFault invalid)
                        (Some OperatorFault.UnsupportedDatabase)
                        "One profile fault covers all mismatches")
        ]
