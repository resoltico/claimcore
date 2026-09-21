module ClaimCore.FuzzQualificationTests.Corpora

open System
open System.Text
open Hedgehog
open Hedgehog.FSharp

/// Hostile input corpora for the boundaries that turn externally supplied bytes into typed values.
/// Sizes stay well inside each boundary's own limit so the property exercises decoding rather than
/// the size guard, and so a shrunk counterexample stays small enough to read.
let private maximumBytes = 4096

let arbitraryBytes =
    Gen.array (Range.linear 0 maximumBytes) (Gen.byte (Range.constantBounded ()))

/// Structure-aware mutation: a valid encoding with one byte flipped, removed, duplicated, or
/// inserted is far more likely to reach a decoder's interior than uniform noise.
let mutated (valid: byte array) =
    if valid.Length = 0 then
        Gen.constant valid
    else
        gen {
            let! index = Gen.int32 (Range.constant 0 (valid.Length - 1))
            let! replacement = Gen.byte (Range.constantBounded ())

            let! operation = Gen.int32 (Range.constant 0 3)

            return
                match operation with
                | 0 ->
                    let copy = Array.copy valid
                    copy[index] <- replacement
                    copy
                | 1 -> Array.append valid[.. index - 1] valid[index + 1 ..]
                | 2 -> Array.concat [ valid[..index]; [| replacement |]; valid[index + 1 ..] ]
                | _ -> valid[..index]
        }

let private utf8 = UTF8Encoding(false, false)

/// JSON-shaped text that is legal to produce but hostile to consume: maximum nesting, oversized
/// numbers, duplicate names, lone surrogates, control characters, and byte-order marks.
let adversarialJson =
    let nested depth =
        String.replicate depth "[" + "0" + String.replicate depth "]"

    let shapes =
        [
            fun () -> nested 63
            fun () -> nested 64
            fun () -> nested 200
            fun () -> "{\"a\":1,\"a\":2}"
            fun () -> "{\"a\":" + String.replicate 400 "9" + "}"
            fun () -> "{\"a\":\"" + String.replicate 2000 "\\u0000" + "\"}"
            fun () -> "{\"a\":\"\\ud800\"}"
            fun () -> "{\"a\":\"\\udfff\\ud800\"}"
            fun () -> "\uFEFF{\"a\":1}"
            fun () -> "{\"a\":1}{\"a\":2}"
            fun () -> "{\"a\":1} trailing"
            fun () -> "{\"a\":1,}"
            fun () -> "// comment\n{\"a\":1}"
            fun () -> "{\"\":1}"
            fun () -> String.replicate 500 "{\"a\":" + "1" + String.replicate 500 "}"
        ]

    gen {
        let! shape = Gen.item shapes
        return utf8.GetBytes(shape ())
    }

/// Byte sequences that are not valid UTF-8, including truncated and overlong encodings.
let invalidUtf8 =
    let shapes =
        [
            [| 0xC3uy |]
            [| 0xE2uy; 0x82uy |]
            [| 0xF0uy; 0x9Fuy; 0x92uy |]
            [| 0xC0uy; 0x80uy |]
            [| 0xEDuy; 0xA0uy; 0x80uy |]
            [| 0xFFuy; 0xFEuy |]
            [| 0x7Buy; 0x22uy; 0x61uy; 0x22uy; 0x3Auy; 0x22uy; 0xFFuy; 0x22uy; 0x7Duy |]
        ]

    Gen.item shapes

/// Opaque-token shapes: wrong length, wrong alphabet, wrong padding, and structurally plausible
/// tokens whose decoded payload is the wrong size.
let cursorText =
    let alphabet =
        Gen.item [ 'A'; 'Z'; 'a'; 'z'; '0'; '9'; '-'; '_'; '+'; '/'; '='; '!'; ' '; '\u0000' ]

    Gen.string (Range.linear 0 96) alphabet

let jsonLike = Gen.choice [ arbitraryBytes; adversarialJson; invalidUtf8 ]
