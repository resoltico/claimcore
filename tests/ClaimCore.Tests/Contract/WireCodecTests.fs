module ClaimCore.Tests.WireCodecTests

open System
open System.Text
open Expecto
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Json

[<NoComparison>]
type private Probe =
    {
        Text: string
        Count: int64
        Enabled: bool
    }

let private probe =
    Codec.record (fun text count enabled ->
        {
            Text = text
            Count = count
            Enabled = enabled
        })
    |> Codec.field "text" _.Text Codec.text
    |> Codec.field "count" _.Count (Codec.integer 0L 50L)
    |> Codec.field "enabled" _.Enabled Codec.boolean
    |> Codec.build
    |> Codec.named "Probe"

let private bytes (value: string) = Encoding.UTF8.GetBytes value
let private read codec value = Codec.decode 65536 codec (bytes value)

let private strict value = StrictJson.read 65536 value

let private byteProfile () =
    for raw in
        [
            ""
            "// comment\n{}"
            "{}{}"
            "{\"x\":1,\"\\u0078\":2}"
            "\"\\ud800\""
            "\"\\uffff\""
            "\"\\udbff\\udfff\""
            "1.0"
            "1e0"
            "9007199254740992"
            "-9007199254740992"
        ] do
        Expect.isError (strict (bytes raw)) "Strict wire bytes refuse"

    Expect.isError (read Codec.text "\"\\u0000\"") "Wire text refuses NUL"
    Expect.isError (strict [| 0xffuy |]) "Fatal UTF8"
    Expect.isError (StrictJson.read 1 (bytes "{}")) "Byte limit"

    let nested count =
        bytes (String('[', count) + "0" + String(']', count))

    Expect.isOk (strict (nested 32)) "Maximum depth"
    Expect.isError (strict (nested 33)) "Beyond maximum depth"

    for raw in [ "-0"; "9007199254740991"; "-9007199254740991"; "\"索赔😀\"" ] do
        Expect.isOk (strict (bytes raw)) "Supported lexical values"

let private privateMemberNames () =
    match StrictJson.read 65536 (bytes "{\"synthetic-private-member\":1.0}") with
    | Ok _ -> failtest "Invalid numeric lexeme was accepted"
    | Error problem ->
        Expect.isFalse
            (problem.Path.Contains("synthetic-private-member"))
            "Byte errors never echo unknown member names"

        Expect.isFalse
            (problem.Reason.Contains("synthetic-private-member"))
            "Fixed reason contains no submitted text"

let private roundTrip () =
    let value =
        {
            Text = "<synthetic-é😀>"
            Count = 50L
            Enabled = true
        }

    let encoded = Codec.encode probe value
    Expect.equal (Codec.decode 65536 probe encoded) (Ok value) "One definition reads and writes"

    Expect.equal
        (Encoding.UTF8.GetString encoded)
        "{\"text\":\"<synthetic-é😀>\",\"count\":50,\"enabled\":true}"
        "Declaration order and raw UTF8"

    for raw in
        [
            "{}"
            "{\"text\":\"x\",\"count\":51,\"enabled\":true}"
            "{\"text\":\"x\",\"count\":1,\"enabled\":true,\"extra\":0}"
            "{\"text\":null,\"count\":1,\"enabled\":true}"
        ] do
        Expect.isError (read probe raw) "Closed typed structure"

let private escaping () =
    let input = "<>&é😀 \"\\\u0001\b\t\n\f\r"
    let expected = "\"<>&é😀 \\\"\\\\\\u0001\\b\\t\\n\\f\\r\""

    Expect.equal
        (Codec.encode Codec.text input)
        (bytes expected)
        "Only quote, backslash and C0 escape"

    Expect.equal (Codec.decode 65536 Codec.text (bytes expected)) (Ok input) "Escapes round trip"

let private primitives () =
    let id = Guid.Parse "a0000000-0000-4000-8000-000000000001"
    Expect.equal (read Codec.uuid $"\"{id:D}\"") (Ok id) "Canonical UUID"

    for raw in [ id.ToString("D").ToUpperInvariant(); Guid.Empty.ToString("D") ] do
        Expect.isError (read Codec.uuid ($"\"{raw}\"")) "Canonical nonzero UUID only"

    let instant =
        DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.FromHours 2.).AddTicks 1234560L

    let encoded = Codec.encode Codec.instant instant

    Expect.equal
        (Encoding.UTF8.GetString encoded)
        "\"2026-01-01T00:00:00.123456Z\""
        "UTC microseconds"

    Expect.equal (Codec.decode 65536 Codec.instant encoded) (Ok instant) "Exact UTC instant"

    for raw in [ "2026-01-01T00:00:00.1234560+00:00"; "2026-02-30T00:00:00.000000Z" ] do
        Expect.isError (read Codec.instant ($"\"{raw}\"")) "Supported instant grammar"

    Expect.isError (read (Codec.list 2 Codec.boolean) "[true,false,true]") "Bounded arrays"

let private namedDefinitions () =
    Expect.equal
        (Map.find "Probe" probe.Definitions)
        (Schema.Object(
            [
                "text", Codec.text.Schema
                "count", (Codec.integer 0L 50L).Schema
                "enabled", Codec.boolean.Schema
            ],
            []
        ))
        "Schema comes from fields"

    Expect.throwsT<ArgumentException>
        (fun () ->
            Codec.record (fun left right -> left, right)
            |> Codec.field "left" fst (Codec.named "Conflicting" Codec.text)
            |> Codec.field "right" snd (Codec.named "Conflicting" Codec.boolean)
            |> Codec.build
            |> ignore)
        "A named definition cannot silently change shape"

let private encoderBounds () =
    let refuses action =
        Expect.throwsT<ArgumentException> action "Encoder and schema enforce the same value set"

    refuses (fun () -> Codec.integer 0L Int64.MaxValue |> ignore)
    refuses (fun () -> Codec.integer 1L 0L |> ignore)
    refuses (fun () -> Codec.encode (Codec.integer 0L 50L) 51L |> ignore)
    refuses (fun () -> Codec.encode Codec.uuid Guid.Empty |> ignore)
    refuses (fun () -> Codec.encode Codec.text "\u0000" |> ignore)
    refuses (fun () -> Codec.encode (Codec.list 1 Codec.boolean) [ true; false ] |> ignore)

    refuses (fun () ->
        Codec.encode Codec.instant (DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks 1L)
        |> ignore)

let private sharedWriters () =
    let value =
        {
            Text = "Rīga 😀 "
            Count = 7L
            Enabled = true
        }

    let tree = probe.Write value
    let wire = WireWriter.bytes tree
    let canonical = CanonicalWriter.bytes tree

    Expect.equal
        (wire |> Encoding.UTF8.GetString)
        "{\"text\":\"Rīga 😀\u2028\",\"count\":7,\"enabled\":true}"
        "Declaration order"

    Expect.equal
        (canonical |> Encoding.UTF8.GetString)
        "{\"count\":7,\"enabled\":true,\"text\":\"Rīga 😀\u2028\"}"
        "Ordinal member order"

    Expect.equal (Codec.decode 65536 probe canonical) (Ok value) "One codec reads canonical bytes"

    Expect.equal
        (StrictJson.read 65536 (WireWriter.indentedBytes tree))
        (Ok tree)
        "Indentation preserves the tree"

    Expect.isFalse
        ((WireWriter.indentedBytes tree |> Encoding.UTF8.GetString).Contains("\\u2028"))
        "Indented strings share raw Unicode escaping"

    let duplicate = JsonValue.Object [ "x", JsonValue.Null; "x", JsonValue.Null ]

    for write in [ WireWriter.bytes; WireWriter.indentedBytes; CanonicalWriter.bytes ] do
        Expect.throws (fun () -> write duplicate |> ignore) "Constructed duplicates refuse"

[<Tests>]
let tests =
    testList
        "standalone typed wire codec"
        [
            testCase "one codec and shared writer rules" sharedWriters
            testCase "encoder value bounds" encoderBounds
            testCase "strict byte profile" byteProfile
            testCase "unknown member names remain private" privateMemberNames
            testCase "closed record round trip" roundTrip
            testCase "raw UTF8 writer profile" escaping
            testCase "lexical primitives and array bounds" primitives
            testCase "derived named schemas refuse collisions" namedDefinitions
        ]
