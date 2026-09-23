module ClaimCore.Tests.FreshRecoveryFormatTests

open System
open System.Security.Cryptography
open System.Text
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Tests.Fixtures

let private request =
    {
        OperationId = Guid.Parse("20000000-0000-4000-8000-000000000901")
        CaseReference = "FORMAT-001"
        ExpectedVersion = 0L
        Command = Command.Open registration
    }

let private digest (bytes: byte array) =
    bytes |> SHA256.HashData |> Convert.ToHexStringLower

let private encoded () = RequestRecord.encode request
let private sourceText () = encoded () |> Encoding.UTF8.GetString
let private decode bytes = RequestRecord.decode 65536 bytes

let private envelope bytes =
    {
        InstallationId = Guid.Parse("10000000-0000-4000-8000-000000000001")
        OperationId = request.OperationId
        CanonicalCommandFormat = RecordVersions.CanonicalCommandFormat
        RequestFingerprintVersion = RecordVersions.RequestFingerprint
        RequestSha256 = digest bytes
        CanonicalRequest = bytes
    }

let private roundTrip =
    testCase
        "[CC-REC-001] fresh canonical and envelope formats preserve exact request identity"
        (fun () ->
            let bytes = encoded ()
            Expect.equal (decode bytes) (Ok request) "Current canonical request"
            let original = envelope bytes

            let restored =
                RecoveryEnvelope.encode original |> RecoveryEnvelope.decode 65536 |> accepted

            Expect.equal restored.CanonicalRequest bytes "No request-byte conversion"
            Expect.equal restored.RequestSha256 original.RequestSha256 "Exact digest"
            Expect.equal restored.InstallationId original.InstallationId "Installation binding"
            Expect.equal restored.CanonicalCommandFormat 3 "Fresh canonical format"
            Expect.equal RecordVersions.Snapshot 2 "Snapshot format is independent"

            Expect.equal
                restored.RequestFingerprintVersion
                1
                "Fingerprint algorithm is independent")

let private oldCanonical =
    testCase
        "[CC-REC-001] historical canonical records are refused rather than re-encoded"
        (fun () ->
            for header in
                [
                    "\"protocolVersion\":2"
                    "\"canonicalCommandFormat\":2"
                    "\"canonicalCommandFormat\":4"
                ] do
                let bytes =
                    (sourceText ()).Replace("\"canonicalCommandFormat\":3", header)
                    |> Encoding.UTF8.GetBytes

                let before = Array.copy bytes
                Expect.isError (decode bytes) "Old and future layouts are unsupported"

                Expect.isError
                    (RecoverySupport.decodeCanonical bytes)
                    "Import refuses the unsupported source"

                Expect.equal bytes before "No compatibility rewrite")

let private oldEnvelope =
    testCase
        "[CC-REC-001] old envelopes and forged new envelopes cannot import old requests"
        (fun () ->
            let current =
                encoded () |> envelope |> RecoveryEnvelope.encode |> Encoding.UTF8.GetString

            for mutation in
                [
                    current.Replace("\"formatVersion\":2", "\"formatVersion\":1")
                    current.Replace("\"canonicalCommandFormat\":3", "\"protocolVersion\":2")
                ] do
                Expect.isError
                    (Encoding.UTF8.GetBytes(mutation) |> RecoveryEnvelope.decode 65536)
                    "Old envelope metadata is refused"

            let old =
                (sourceText ()).Replace("\"canonicalCommandFormat\":3", "\"protocolVersion\":2")
                |> Encoding.UTF8.GetBytes

            let forged = envelope old |> RecoveryEnvelope.encode

            Expect.isError
                (RecoveryEnvelope.decode 65536 forged)
                "A correct SHA-256 and current wrapper cannot authorize old canonical bytes")

let private exactImport =
    testCase
        "[CC-REC-001] noncanonical layouts may decode but cannot enter exact recovery retention"
        (fun () ->
            let spaced = Encoding.UTF8.GetBytes(" " + sourceText ())

            Expect.equal
                (decode spaced)
                (Ok request)
                "Decoder can describe semantically valid input"

            Expect.isError
                (RecoverySupport.decodeCanonical spaced)
                "Recovery does not silently canonicalize"

            Expect.isError
                (envelope spaced |> RecoveryEnvelope.encode |> RecoveryEnvelope.decode 65536)
                "Envelope must preserve canonical rather than merely equivalent bytes")

let private tampering =
    testCase
        "[CC-REC-001] fresh envelopes reject digest tampering and operation substitution"
        (fun () ->
            let original = encoded () |> envelope

            for changed in
                [
                    { original with
                        RequestSha256 = String.replicate 64 "0"
                    }
                    { original with
                        OperationId = Guid.NewGuid()
                    }
                ] do
                Expect.isError
                    (RecoveryEnvelope.encode changed |> RecoveryEnvelope.decode 65536)
                    "Identity validation remains mandatory after the clean break")

let tests =
    testList
        "fresh recovery format boundary"
        [ roundTrip; oldCanonical; oldEnvelope; exactImport; tampering ]
