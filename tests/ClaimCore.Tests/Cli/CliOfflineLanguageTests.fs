module ClaimCore.Tests.CliOfflineLanguageTests

open ClaimCore.CommandLine

open System
open System.IO
open System.Text
open System.Collections.Generic
open Expecto

let private qualify directory =
    let create language =
        let root = Path.Combine(directory, "language-profile-" + language)
        let directory = Path.Combine(root, "claimcore")

        Directory.CreateDirectory(
            directory,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )
        |> ignore

        let path = Path.Combine(directory, "profile.json")
        File.WriteAllText(path, $"{{\"language\":\"{language}\"}}")
        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

        root

    let profileLv = create "lv"
    let profileAr = create "ar"
    let empty = Path.Combine(directory, "language-profile-empty")

    let environment root values =
        let result = Dictionary<string, string>()

        for name, value in
            [
                "XDG_CONFIG_HOME", root
                "CLAIMCORE_LANGUAGE", Unchecked.defaultof<string>
                "LC_ALL", ""
                "LC_MESSAGES", ""
                "LANG", "en_US.UTF-8"
            ]
            @ values do
            result[name] <- value

        result

    let expected =
        CliOffline.invoke [ "schema"; "invocation" ] (environment empty []) None

    Expect.equal expected.ExitCode 0 "Known machine schema succeeds without connection settings"

    for source, selected in
        [
            "environment", environment profileAr [ "CLAIMCORE_LANGUAGE", "lv" ]
            "profile", environment profileLv [ "LC_ALL", "ar_EG.UTF-8" ]
            "LC_ALL", environment empty [ "LC_ALL", "lv_LV.UTF-8" ]
            "LC_MESSAGES", environment empty [ "LC_MESSAGES", "lv_LV.UTF-8"; "LANG", "ar_EG.UTF-8" ]
        ] do
        let failed = CliOffline.invoke [ "schema"; "unknown-l7-native" ] selected None

        Expect.equal failed.ExitCode 64 ("Late schema lookup follows " + source)

        Expect.equal
            failed.StandardOutput.Length
            0
            "Human failure does not fabricate machine output"

        let text = Encoding.UTF8.GetString failed.StandardError
        Expect.stringContains text "Nezināma komanda" "Independent Latvian failure expectation"

        Expect.stringContains
            text
            "unknown-l7-native"
            "Localized failure retains the authored selector"

        Expect.isFalse
            (text.Contains(directory, StringComparison.Ordinal))
            "Public profile location is never exposed"

        let machine = CliOffline.invoke [ "schema"; "invocation" ] selected None

        Expect.equal machine.ExitCode 0 "Successful machine schema is not localized"
        Expect.equal machine.StandardError.Length 0 "Successful schema has no human diagnostics"

        Expect.equal
            machine.StandardOutput
            expected.StandardOutput
            "Language selection never changes exact schema JSON bytes"

    let illegal =
        CliOffline.invoke
            [ "schema"; "unknown-l7-native"; "--language"; "lv" ]
            (environment profileAr [])
            None

    Expect.equal illegal.ExitCode 64 "Schema still accepts no language flag"
    Expect.equal illegal.StandardOutput.Length 0 "Illegal flags leave machine stdout empty"

    Expect.stringContains
        (Encoding.UTF8.GetString illegal.StandardError)
        "Schema pieņem"
        "The existing illegal-option usage error still follows explicit language"

[<Tests>]
let tests =
    testCase "offline CLI schema failure language precedence" (fun () ->
        let directory = Directory.CreateTempSubdirectory "claimcore-offline-language-"

        try
            qualify directory.FullName
        finally
            directory.Delete true)
    |> testSequenced
