module ClaimCore.Tests.CursorTests

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.TestSupport

let private text name (value: JsonElement) =
    value.GetProperty(name: string).GetString() |> nonNull

let private actor = Guid.Parse "a0000000-0000-4000-8000-000000000001"

let private token (json: string) =
    Convert
        .ToBase64String(Encoding.UTF8.GetBytes json)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_')

let private examples action =
    use fixture =
        JsonDocument.Parse(
            File
                .ReadAllBytes(
                    Path.Combine(RepositoryRoot.find (), "tests/fixtures/cursor-examples.json")
                )
                .AsMemory()
        )

    action fixture.RootElement

let private accepted () =
    examples (fun fixture ->
        for sample in fixture.GetProperty("accepted").EnumerateArray() do
            let encoded = text "token" sample
            use json = JsonDocument.Parse(text "canonicalJson" sample)
            let position = json.RootElement.GetProperty "after"

            let actual =
                match text "endpoint" sample with
                | "case.list" ->
                    let expected = position.GetString() |> nonNull

                    Expect.equal
                        (Cursors.decodeCases encoded)
                        (Ok expected)
                        "Independent case position"

                    Cursors.encodeCases expected
                | "case.history" ->
                    Expect.equal
                        (Cursors.decodeHistory "demo-0001" encoded)
                        (Ok(position.GetInt64()))
                        "Independent history position"

                    Cursors.encodeHistory "DEMO-0001" (position.GetInt64())
                | "authority.list" ->
                    let expected = Guid.Parse(position.GetString() |> nonNull)

                    Expect.equal
                        (Cursors.decodeActors encoded)
                        (Ok expected)
                        "Independent actor position"

                    Cursors.encodeActors expected
                | "authority.grants" ->
                    let caseId = position.GetProperty "caseId"

                    let expected =
                        {
                            Role = Roles.tryParse (text "role" position) |> Option.get
                            CaseId =
                                if caseId.ValueKind = JsonValueKind.Null then
                                    None
                                else
                                    Some(Guid.Parse(caseId.GetString() |> nonNull))
                        }

                    Expect.equal
                        (Cursors.decodeGrants actor encoded)
                        (Ok expected)
                        "Independent grant position"

                    Cursors.encodeGrants actor expected
                | _ -> failtest "Unknown fixture endpoint"

            Expect.equal actual encoded "Encoder reproduces committed owner bytes")

let private refused () =
    examples (fun fixture ->
        for sample in fixture.GetProperty("refused").EnumerateArray() do
            let name = text "name" sample
            let encoded = text "token" sample

            let decoded =
                if name.StartsWith("history cursor", StringComparison.Ordinal) then
                    Cursors.decodeHistory "DEMO-0002" encoded |> Result.map string
                elif name = "revision zero" then
                    Cursors.decodeHistory "DEMO-0001" encoded |> Result.map string
                else
                    Cursors.decodeCases encoded

            Expect.equal decoded (Error Rejection.InvalidCursor) name)

let private boundaries () =
    for value in [ ""; "!"; "A"; String('A', 513) ] do
        Expect.equal (Cursors.decodeCases value) (Error Rejection.InvalidCursor) "Token bounds"

    for json in
        [
            "{\"after\":\"DEMO\",\"kind\":\"CASES\",\"v\":1.0}"
            "{\"after\":\"DEMO\",\"after\":\"DEMO\",\"kind\":\"CASES\",\"v\":1}"
            "{\"after\":\"\\ud800\",\"kind\":\"CASES\",\"v\":1}"
        ] do
        Expect.equal
            (Cursors.decodeCases (token json))
            (Error Rejection.InvalidCursor)
            "Strict canonical JSON"

    for role, caseId in
        [
            "UNKNOWN", "null"
            "OWNER", "\"00000000-0000-0000-0000-000000000000\""
            "CASE_READER", "\"A0000000-0000-4000-8000-000000000001\""
        ] do
        let raw =
            $"{{\"actor\":\"{actor:D}\",\"after\":{{\"caseId\":{caseId},\"role\":\"{role}\"}},\"kind\":\"GRANTS\",\"v\":1}}"

        Expect.equal
            (Cursors.decodeGrants actor (token raw))
            (Error Rejection.InvalidCursor)
            "Admitted role and UUID only"

let private maximum () =
    let reference = String('"', 80)
    let encoded = Cursors.encodeHistory reference Revision.Maximum
    Expect.isLessThan encoded.Length 512 "Largest escaped history position fits"

    Expect.equal
        (Cursors.decodeHistory reference encoded)
        (Ok Revision.Maximum)
        "Maximum safe revision"

    Expect.equal
        (Cursors.decodeHistory "OTHER" encoded)
        (Error Rejection.InvalidCursor)
        "Resource binding"

    let grant = Cursors.encodeGrants actor { Role = Role.Owner; CaseId = None }

    Expect.equal
        (Cursors.decodeGrants (Guid.NewGuid()) grant)
        (Error Rejection.InvalidCursor)
        "Actor binding"

[<Tests>]
let tests =
    testList
        "plain canonical keysets"
        [
            testCase "owner accepted examples" accepted
            testCase "owner refused examples" refused
            testCase "strict token and value boundaries" boundaries
            testCase "maximum positions and target binding" maximum
        ]
