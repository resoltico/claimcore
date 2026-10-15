module ClaimCore.Tests.CursorRecordTests

open System
open Expecto
open ClaimCore.RecordFormat

let private key = [| 0uy .. 31uy |]
let private binding = [| 32uy .. 63uy |]

let private envelope =
    {
        IssuedAtTicks = DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero).UtcTicks
        Binding = binding
        Position = "DEMO-01"
    }

let private roundTrip () =
    let token = CursorRecord.encode key envelope

    Expect.equal
        (CursorRecord.decode key token)
        (Ok envelope)
        "Authenticated envelope retains exact values"

    Expect.equal (CursorRecord.encode key envelope) token "The byte format is deterministic"

    for issued in [ DateTimeOffset.MinValue.UtcTicks; DateTimeOffset.MaxValue.UtcTicks ] do
        let value = { envelope with IssuedAtTicks = issued }

        Expect.equal
            (CursorRecord.decode key (CursorRecord.encode key value))
            (Ok value)
            "Representable instant boundary"

    let boundary =
        { envelope with
            Position = String.replicate 64 "😀"
        }

    Expect.equal
        (CursorRecord.decode key (CursorRecord.encode key boundary))
        (Ok boundary)
        "Position limit counts UTF-8 bytes"

let private refusals () =
    let token = CursorRecord.encode key envelope

    for candidate in [ ""; " "; token + "="; token + "\n"; String.replicate 513 "a"; "a"; "____" ] do
        Expect.isError (CursorRecord.decode key candidate) "Malformed or oversized token refuses"

    for invalid in
        [
            [||]
            Array.zeroCreate 31
            Array.zeroCreate 33
            Unchecked.defaultof<byte array>
        ] do
        Expect.isError (CursorRecord.decode invalid token) "Key width is fixed"

        Expect.throws
            (fun () -> CursorRecord.encode invalid envelope |> ignore)
            "Invalid signing key refuses"

    Expect.isError
        (CursorRecord.decode (Array.rev key) token)
        "A different runtime key cannot authenticate"

    for position in
        [
            ""
            String.replicate 257 "a"
            "a\u0000b"
            String([| char 0xD800 |])
            String([| char 0xFFFF |])
        ] do
        Expect.throws
            (fun () -> CursorRecord.encode key { envelope with Position = position } |> ignore)
            "Position representation is bounded"

    for ticks in [ -1L; DateTimeOffset.MaxValue.UtcTicks + 1L ] do
        Expect.throws
            (fun () -> CursorRecord.encode key { envelope with IssuedAtTicks = ticks } |> ignore)
            "Issued instant is representable"

    for hash in [ [||]; Array.zeroCreate 31; Array.zeroCreate 33 ] do
        Expect.throws
            (fun () -> CursorRecord.encode key { envelope with Binding = hash } |> ignore)
            "Binding has digest width"

let private tamper () =
    let token = CursorRecord.encode key envelope

    for index in 0 .. token.Length - 1 do
        let chars = token.ToCharArray()
        chars[index] <- if chars[index] = 'A' then 'B' else 'A'

        Expect.isError
            (CursorRecord.decode key (String chars))
            "Every encoded component is authenticated"
    // Seven position bytes make two trailing base64 characters; nonzero unused bits are noncanonical.
    let alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"
    let chars = token.ToCharArray()
    let index = alphabet.IndexOf chars[chars.Length - 1]
    chars[chars.Length - 1] <- alphabet[index + 1]
    Expect.isError (CursorRecord.decode key (String chars)) "Alternate base64 spelling is refused"

[<Tests>]
let tests =
    testList
        "authenticated cursor bytes"
        [
            testCase "envelopes retain exact data and bounded instants" roundTrip
            testCase "malformed encodings and invalid envelope values refuse" refusals
            testCase "every token component and canonical base64 spelling are checked" tamper
        ]
