module ClaimCore.AcceptanceTests.NarrowRoleAcceptanceTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Collections.Generic
open Expecto

open ClaimCore.AcceptanceTests.NativeRoleFixture

let private readProof reference unrelated role scoped =
    let frames =
        [
            frameRead "case.get" {| caseReference = reference |}
            frameRead
                "case.history"
                {|
                    caseReference = reference
                    detail = "SUMMARY"
                    limit = 50
                |}
            frameRead
                "case.history"
                {|
                    caseReference = reference
                    detail = "FULL"
                    limit = 50
                |}
            frameRead "case.get" {| caseReference = unrelated |}
        ]

    let replies = narrowReplies frames
    let tags = replies |> Array.map (fun value -> value.GetProperty("tag").GetString())
    Expect.equal tags[0] "SUCCEEDED" "Granted case read succeeds without wider authority"
    Expect.equal tags[1] "SUCCEEDED" "Granted SUMMARY is readable"

    Expect.equal
        tags[2]
        (if role = "CASE_EDITOR" then "SUCCEEDED" else "REJECTED")
        "FULL remains separately authorized"

    Expect.equal
        tags[3]
        (if scoped then "REJECTED" else "SUCCEEDED")
        "Unrelated case remains outside a case-only grant"

    let current = replies[0].GetProperty("data").GetProperty("current")

    Expect.equal
        (current.GetProperty("availableCommands").GetArrayLength() > 0)
        (role = "CASE_EDITOR")
        "Actor advice does not invent editing authority"

let private verifyResolution (resolution: JsonElement) =
    Expect.equal
        (resolution.GetProperty("tag").GetString())
        "COMPLETED"
        "Exact native resolution is definite"

    let data = resolution.GetProperty("data")

    Expect.equal
        (data.GetProperty("execution").GetProperty("tag").GetString())
        "ACCEPTED"
        "No editing grant was needed to resolve"

    Expect.equal
        (data.GetProperty("settlement").GetString())
        "CONFIRMED"
        "Acceptance is independently settled"


let private recoveryProof reference operation digest role =
    if role = "RECOVERY_OPERATOR" then
        let replies =
            narrowReplies
                [
                    RemoteFixture.encode
                        "recovery.inspect"
                        {|
                            operationId = operation
                            attemptLimit = 10
                        |}
                    RemoteFixture.encode
                        "recovery.resolve"
                        {|
                            operationId = operation
                            requestSha256 = digest
                        |}
                ]

        Expect.equal
            (replies[0].GetProperty("tag").GetString())
            "SUCCEEDED"
            "Known operation inspection is authorized"

        verifyResolution replies[1]

        let read = outcome "case.get" {| caseReference = reference |}

        Expect.equal
            (read
                .GetProperty("data")
                .GetProperty("current")
                .GetProperty("case")
                .GetProperty("revision")
                .GetString())
            "2"
            "Independent native read proves one accepted effect"
    else
        let replies =
            narrowReplies [ RemoteFixture.encode "case.get" {| caseReference = reference |} ]

        Expect.equal
            (replies[0].GetProperty("tag").GetString())
            "REJECTED"
            "Other legitimate roles do not gain case-read authority"

let private qualify role scoped () =
    let reference = "NATIVE-NARROW-" + RemoteFixture.id ()
    let unrelated = "NATIVE-UNRELATED-" + RemoteFixture.id ()
    seed unrelated |> ignore
    let operation, digest = seed reference
    configure reference role scoped

    try
        if role = "CASE_READER" || role = "CASE_EDITOR" then
            readProof reference unrelated role scoped
        elif role = "RECOVERY_EXPORTER" then
            NativeRoleExportProof.qualify operation digest
        else
            recoveryProof reference operation digest role
    finally
        restore reference role scoped

let tests =
    [
        for role, scoped in
            [
                "CASE_READER", false
                "CASE_READER", true
                "CASE_EDITOR", true
                "RECOVERY_OPERATOR", false
                "RECOVERY_OPERATOR", true
                "RECOVERY_EXPORTER", false
                "DATA_STEWARD", false
                "NONE", false
            ] do
            testCase
                ($"[CC-AUTH-001][CC-CLI-001] published native {role} scoped={scoped} preserves narrow authority")
                (qualify role scoped)
    ]
