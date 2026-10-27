module ClaimCore.Tests.CanonicalJsonTests

open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Json

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.Tests.PropertyHarness

let private admitted (text: string) =
    Encoding.UTF8.GetBytes text |> StrictJson.read Int32.MaxValue

let private canonical text =
    admitted text |> Fixtures.accepted |> CanonicalWriter.bytes

let private text (element: JsonElement) =
    element.GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failtest "Missing vector text")

let private vectorTests =
    testCase "independent RFC sorting and string vectors match exact UTF-8" (fun () ->
        let path =
            Path.Combine(RepositoryRoot.find (), "tests/fixtures/record-format-vectors.json")

        use document = JsonDocument.Parse(File.ReadAllBytes(path))
        let vectors = document.RootElement.GetProperty("jcs").EnumerateArray() |> Seq.toList
        Expect.equal vectors.Length 5 "All independent JCS vectors execute"

        for vector in vectors do
            let expected = vector.GetProperty("canonicalHex") |> text |> Convert.FromHexString

            let actual = canonical (vector.GetProperty("canonicalUtf8") |> text)
            Expect.isTrue (actual = expected) "Independent canonical bytes")

let private refusedJson =
    [
        "9007199254740992"
        "-9007199254740992"
        "-9223372036854775808"
        "1.5"
        "1.0"
        "1e0"
        "\"\\ud800\""
        "\"\\udc00\""
        "{\"a\":1,\"a\":2}"
        "{\"a\":1,\"\\u0061\":2}"
        "\"\\ufdd0\""
        "\"\\uffff\""
        "\"\\udbff\\udfff\""
        "{\"\\ufffe\":0}"
    ]

let private refusalTests =
    testCase
        "unsafe integers, noninteger lexemes, invalid Unicode and duplicate names refuse"
        (fun () ->
            for value in refusedJson do
                Expect.isError
                    (admitted value)
                    "Invalid I-JSON returns a typed refusal without throwing"

            for value in [ Int64.MinValue; -9007199254740992L; 9007199254740992L; Int64.MaxValue ] do
                Expect.throws
                    (fun () -> CanonicalWriter.bytes (JsonValue.Integer value) |> ignore)
                    "Closed values enforce safe bounds too"

            Expect.throws
                (fun () ->
                    CanonicalWriter.bytes (JsonValue.String(String([| char 0xD800 |]))) |> ignore)
                "Constructed malformed strings refuse")

let private cultureTest =
    testCase "integer and escape bytes are invariant under ambient culture" (fun () ->
        let previous = CultureInfo.CurrentCulture
        let custom = CultureInfo.InvariantCulture.Clone() :?> CultureInfo
        custom.NumberFormat.NegativeSign <- "~"

        try
            CultureInfo.CurrentCulture <- custom

            Expect.equal
                (canonical "-9007199254740991" |> Encoding.UTF8.GetString)
                "-9007199254740991"
                "Invariant negative number"

            Expect.equal (canonical "-0" |> Encoding.UTF8.GetString) "0" "Negative zero is zero"

            Expect.equal
                (canonical "\"\\u0000\"" |> Encoding.UTF8.GetString)
                "\"\\u0000\""
                "Generic JSON supports NUL"
        finally
            CultureInfo.CurrentCulture <- previous)

let private values =
    gen {
        let! text =
            Gen.item [ "Rīga مطالبة 😀"; "A<B>&'+`"; "\u0000\u0007\b\t\n\r"; "é"; "\\\"" ]

        let! number = Gen.int64 (Range.constant -9007199254740991L 9007199254740991L)
        let! flag = Gen.bool

        return
            JsonValue.Object
                [
                    "z",
                    JsonValue.Array
                        [ JsonValue.Integer number; JsonValue.Boolean flag; JsonValue.Null ]
                    "a", JsonValue.String text
                ]
    }

let private roundTrip =
    property {
        let! value = values
        let encoded = CanonicalWriter.bytes value
        return canonical (Encoding.UTF8.GetString encoded) = encoded
    }

let private alternateSpelling (encoded: byte array) =
    use document = JsonDocument.Parse(ReadOnlyMemory<byte>(encoded))
    let members = document.RootElement.EnumerateObject() |> Seq.rev |> Seq.toList

    let alternate =
        members
        |> List.map (fun item ->
            JsonSerializer.Serialize(item.Name)
            + " : "
            + JsonSerializer.Serialize(item.Value))
        |> String.concat ",\n"

    "{\n" + alternate + "\n}"

let private respelling =
    property {
        let! value = values
        let encoded = CanonicalWriter.bytes value
        return canonical (alternateSpelling encoded) = encoded
    }

[<Tests>]
let tests =
    testList
        "canonical JSON"
        [
            vectorTests
            refusalTests
            cultureTest
            testCase "canonical parse is idempotent over 200 fixed cases" (fun () ->
                run "canonical JSON round trip" roundTrip)
            testCase "layout, escapes and member order preserve 200 identities" (fun () ->
                run "canonical JSON spelling" respelling)
        ]
