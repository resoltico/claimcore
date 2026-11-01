module ClaimCore.Tests.CursorTests

open ClaimCore.RecordFormat

open ClaimCore.Protocol

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.TestSupport

let private text name (value: JsonElement) =
    value.GetProperty(name: string).GetString() |> nonNull

let private filter = CaseListFilter.empty<CaseSearchText>

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
                    let expected = CaseListPosition.Reference(text "reference" position)

                    Expect.equal
                        (Cursors.decodeCases
                            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                            filter
                            encoded)
                        (Ok expected)
                        "Independent case position"

                    Cursors.encodeCases
                        (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                        filter
                        expected
                | "case.history" ->
                    Expect.equal
                        (Cursors.decodeHistory
                            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                            (CaseFixtures.caseId actor)
                            encoded)
                        (Ok(CaseFixtures.revision (position.GetInt64())))
                        "Independent history position"

                    Cursors.encodeHistory
                        (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                        (CaseFixtures.caseId actor)
                        (CaseFixtures.revision (position.GetInt64()))
                | "authority.list" ->
                    let expected = Guid.Parse(position.GetString() |> nonNull)

                    Expect.equal
                        (Cursors.decodeActors
                            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                            encoded)
                        (Ok(CaseFixtures.actorId (expected)))
                        "Independent actor position"

                    Cursors.encodeActors
                        (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                        (CaseFixtures.actorId (expected))
                | "authority.grants" ->
                    let caseId = position.GetProperty "caseId"

                    let expected =
                        {
                            Role = Role.parse (text "role" position) |> Option.get
                            CaseId =
                                if caseId.ValueKind = JsonValueKind.Null then
                                    None
                                else
                                    Some(
                                        CaseFixtures.caseId (
                                            Guid.Parse(caseId.GetString() |> nonNull)
                                        )
                                    )
                        }

                    Expect.equal
                        (Cursors.decodeGrants
                            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                            (CaseFixtures.actorId (actor))
                            encoded)
                        (Ok expected)
                        "Independent grant position"

                    Cursors.encodeGrants
                        (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                        (CaseFixtures.actorId (actor))
                        expected
                | "authority.history" ->
                    let target = json.RootElement.GetProperty "actor"

                    let actor =
                        if target.ValueKind = JsonValueKind.Null then
                            None
                        else
                            Some(CaseFixtures.actorId (Guid.Parse(target.GetString() |> nonNull)))

                    let expected = CaseFixtures.sequence (position.GetInt64())

                    Expect.equal
                        (Cursors.decodeAuthorityHistory
                            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                            actor
                            encoded)
                        (Ok expected)
                        "Independent authority-history position and target"

                    Cursors.encodeAuthorityHistory
                        (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                        actor
                        expected
                | _ -> failtest "Unknown fixture endpoint"

            Expect.equal actual encoded "Encoder reproduces committed owner bytes")

let private refused () =
    examples (fun fixture ->
        for sample in fixture.GetProperty("refused").EnumerateArray() do
            let name = text "name" sample
            let encoded = text "token" sample

            let decoded =
                if name.StartsWith("history cursor", StringComparison.Ordinal) then
                    Cursors.decodeHistory
                        (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                        (CaseFixtures.caseId (Guid.Parse "b0000000-0000-4000-8000-000000000001"))
                        encoded
                    |> Result.map string
                elif name = "revision zero" then
                    Cursors.decodeHistory
                        (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                        (CaseFixtures.caseId actor)
                        encoded
                    |> Result.map string
                else
                    Cursors.decodeCases
                        (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                        filter
                        encoded
                    |> Result.map CaseListPosition.reference

            Expect.equal decoded (Error Refusal.InvalidCursor) name)

let private boundaries () =
    for value in [ ""; "!"; "A"; String('A', Paging.MaximumCaseCursorCharacters + 1) ] do
        Expect.equal
            (Cursors.decodeCases
                (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                filter
                value)
            (Error Refusal.InvalidCursor)
            "Token bounds"

    let baseJson =
        examples (fun fixture ->
            fixture.GetProperty("accepted").EnumerateArray()
            |> Seq.find (fun row -> text "endpoint" row = "case.list")
            |> text "canonicalJson")

    for json in
        [
            baseJson.Replace("\"v\":5", "\"v\":5.0")
            baseJson.Replace("\"kind\":\"CASES\"", "\"kind\":\"CASES\",\"kind\":\"CASES\"")
            baseJson.Replace("DEMO-0050", "\\ud800")
        ] do
        Expect.notEqual json baseJson "Each rejection control actually changes the accepted token"

        Expect.equal
            (Cursors.decodeCases
                (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                filter
                (token json))
            (Error Refusal.InvalidCursor)
            "Strict canonical JSON"

    for role, caseId in
        [
            "UNKNOWN", "null"
            "OWNER", "\"00000000-0000-0000-0000-000000000000\""
            "CASE_READER", "\"A0000000-0000-4000-8000-000000000001\""
        ] do
        let raw =
            $"{{\"actor\":\"{actor:D}\",\"after\":{{\"caseId\":{caseId},\"role\":\"{role}\"}},\"installationId\":\"90000000-0000-4000-8000-000000000001\",\"kind\":\"GRANTS\",\"v\":2}}"

        Expect.equal
            (Cursors.decodeGrants
                (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                (CaseFixtures.actorId (actor))
                (token raw))
            (Error Refusal.InvalidCursor)
            "Admitted role and UUID only"

let private maximum () =
    let encoded =
        Cursors.encodeHistory
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            (CaseFixtures.caseId actor)
            (CaseFixtures.revision 9007199254740991L)

    Expect.isLessThan encoded.Length 512 "Largest installation-bound history position fits"

    Expect.equal
        (Cursors.decodeHistory
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            (CaseFixtures.caseId actor)
            encoded)
        (Ok(CaseFixtures.revision 9007199254740991L))
        "Maximum safe revision"

    Expect.equal
        (Cursors.decodeHistory
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            (CaseFixtures.caseId (Guid.Parse "b0000000-0000-4000-8000-000000000001"))
            encoded)
        (Error Refusal.InvalidCursor)
        "Resource binding"

    let grant =
        Cursors.encodeGrants
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            (CaseFixtures.actorId (actor))
            { Role = Role.Owner; CaseId = None }

    Expect.equal
        (Cursors.decodeGrants
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            (CaseFixtures.actorId (Guid.NewGuid()))
            grant)
        (Error Refusal.InvalidCursor)
        "Actor binding"

let private worklistBinding () =
    let admitted input =
        CaseListFilter.admit input
        |> Result.defaultWith (fun _ -> failtest "Search fixture must admit")

    let selected =
        CaseListFilter.create
            [ Lifecycle.Active ]
            []
            [ CaseProgress.Decided ]
            (Some "RĪGA")
            CaseListOrder.Recent
        |> admitted

    let position =
        CaseListPosition.Recent(DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), "CASE-2")

    let encoded =
        Cursors.encodeCases
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            selected
            position

    Expect.equal
        (Cursors.decodeCases
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            selected
            encoded)
        (Ok position)
        "Recent timestamp and tie-break reference survive"

    for changed in
        [
            { selected with
                Lifecycles = [ Lifecycle.Voided ]
            }
            { selected with
                Statuses = [ CaseStatus.Closed ]
            }
            { selected with
                Progress = [ CaseProgress.Paid ]
            }
            { selected with Text = None }
            { selected with
                Assignment = Some CaseAssignment.Mine
            }
            { selected with
                Order = CaseListOrder.Reference
            }
        ] do
        Expect.equal
            (Cursors.decodeCases
                (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                changed
                encoded)
            (Error Refusal.InvalidCursor)
            "Every filter and order binds the cursor"

    let compatibility =
        CaseListFilter.create [] [] [] (Some "¨") CaseListOrder.Reference |> admitted

    let cursor =
        Cursors.encodeCases
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            compatibility
            (CaseListPosition.Reference "CASE-1")

    Expect.equal
        (Cursors.decodeCases
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            compatibility
            cursor)
        (Ok(CaseListPosition.Reference "CASE-1"))
        "Normalized compatibility text is compared without re-admitting it as authored input"

let private recentPrecision () =
    let filter =
        { CaseListFilter.empty<CaseSearchText> with
            Order = CaseListOrder.Recent
        }

    let instant = DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero)
    let position = CaseListPosition.Recent(instant, "CASE-1")

    Expect.equal
        (Cursors.decodeCases
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            filter
            (Cursors.encodeCases
                (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                filter
                position))
        (Ok position)
        "Whole-microsecond instants remain valid"

    Expect.throwsT<ArgumentException>
        (fun () ->
            Cursors.encodeCases
                (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                filter
                (CaseListPosition.Recent(instant.AddTicks 1L, "CASE-1"))
            |> ignore)
        "An encoder cannot produce an unsupported recorded instant"

    let raw =
        """{"after":{"changedAt":"2026-10-28T00:00:00.0000001+00:00","reference":"CASE-1"},"filters":{"assignment":null,"lifecycle":[],"order":"RECENT","progress":[],"status":[],"text":null},"installationId":"90000000-0000-4000-8000-000000000001","kind":"CASES","v":4}"""

    Expect.equal
        (Cursors.decodeCases
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            filter
            (token raw))
        (Error Refusal.InvalidCursor)
        "A forged canonical cursor cannot introduce sub-microsecond keyset semantics"

[<Tests>]
let tests =
    testList
        "plain canonical keysets"
        [
            testCase "worklist filters bind every cursor dimension" worklistBinding
            testCase "recent positions retain recorded-instant precision" recentPrecision
            testCase "owner accepted examples" accepted
            testCase "owner refused examples" refused
            testCase "strict token and value boundaries" boundaries
            testCase "maximum positions and target binding" maximum
        ]

[<Tests>]
let triageCursors =
    testCase "notification cursors retain null dates and authority cursors reject zero" (fun () ->
        let selected =
            { CaseListFilter.empty<CaseSearchText> with
                Order = CaseListOrder.Notified
                Assignment = Some CaseAssignment.Mine
            }

        for date in [ None; Some(CaseFixtures.date "2026-08-03") ] do
            let expected = CaseListPosition.Notified(date, "CASE-2")

            Expect.equal
                (Cursors.decodeCases
                    (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                    selected
                    (Cursors.encodeCases
                        (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                        selected
                        expected))
                (Ok expected)
                "Live and erased notification positions round trip"

        let zero =
            token
                """{"actor":null,"after":0,"installationId":"90000000-0000-4000-8000-000000000001","kind":"AUTHORITY_HISTORY","v":2}"""

        Expect.equal
            (Cursors.decodeAuthorityHistory
                (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                None
                zero)
            (Error Refusal.InvalidCursor)
            "An empty head is never a history continuation")

[<Tests>]
let installationBinding =
    testCase
        "every continuation refuses a different installation after a valid positive control"
        (fun () ->
            examples (fun fixture ->
                let foreign = Guid.Parse "90000000-0000-4000-8000-000000000099"

                for sample in fixture.GetProperty("accepted").EnumerateArray() do
                    let encoded = text "token" sample

                    let result =
                        match text "endpoint" sample with
                        | "case.list" ->
                            Cursors.decodeCases foreign filter encoded |> Result.map ignore
                        | "case.history" ->
                            Cursors.decodeHistory foreign (CaseFixtures.caseId actor) encoded
                            |> Result.map ignore
                        | "authority.list" ->
                            Cursors.decodeActors foreign encoded |> Result.map ignore
                        | "authority.grants" ->
                            Cursors.decodeGrants foreign (CaseFixtures.actorId actor) encoded
                            |> Result.map ignore
                        | "authority.history" ->
                            use json = JsonDocument.Parse(text "canonicalJson" sample)
                            let target = json.RootElement.GetProperty("actor")

                            let id =
                                if target.ValueKind = JsonValueKind.Null then
                                    None
                                else
                                    Some(
                                        CaseFixtures.actorId (
                                            Guid.Parse(target.GetString() |> nonNull)
                                        )
                                    )

                            Cursors.decodeAuthorityHistory foreign id encoded |> Result.map ignore
                        | _ -> failtest "Unknown independent cursor fixture"

                    Expect.equal result (Error Refusal.InvalidCursor) "Installation-bound grammar"))
