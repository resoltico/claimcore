module ClaimCore.Tests.CheckpointRecordTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat

let private text hash frontier =
    "{\"installationId\":\"10000000-0000-4000-8000-000000000001\",\"operationSequence\":7,\"operationHash\":\""
    + hash
    + "\",\"expiredBefore\":"
    + frontier
    + "}"

[<Tests>]
let tests =
    testList
        "operator checkpoint format"
        [
            testCase "authored checkpoint retains prefix hash and sub-millisecond cutoff" (fun () ->
                let authored = text (String.replicate 32 "ab") "\"2026-11-09T12:00:00.0000001Z\""

                let checkpoint =
                    CheckpointRecord.decode (Encoding.UTF8.GetBytes authored)
                    |> Result.defaultWith (fun error -> failtestf "%A" error)

                Expect.equal (Sequence.value checkpoint.Position.Sequence) 7L "Independent prefix"

                Expect.equal
                    (EntryHash.toBytes checkpoint.Position.EntryHash)
                    (Array.create 32 0xabuy)
                    "Exact hash bytes"

                let cutoff = checkpoint.ExpiredBefore |> Option.get
                Expect.equal cutoff.UtcTicks 639298224000000001L "Independent Gregorian UTC ticks"

                Expect.equal
                    (CheckpointRecord.decode (CheckpointRecord.encode checkpoint))
                    (Ok checkpoint)
                    "Canonical file preserves full precision")
            testCase
                "nullable initial frontier is explicit and old expectations are not checkpoints"
                (fun () ->
                    let current = text (String.replicate 32 "ab") "null" |> Encoding.UTF8.GetBytes
                    Expect.isOk (CheckpointRecord.decode current) "No confirmed expiry yet"

                    for candidate in
                        [
                            "{\"sequence\":7,\"entryHash\":\"" + String.replicate 32 "ab" + "\"}"
                            Encoding.UTF8.GetString(current).Replace(",\"expiredBefore\":null", "")
                            Encoding.UTF8
                                .GetString(current)
                                .Replace(
                                    "10000000-0000-4000-8000-000000000001",
                                    "00000000-0000-0000-0000-000000000000"
                                )
                            Encoding.UTF8
                                .GetString(current)
                                .Replace(
                                    "\"operationSequence\":7",
                                    "\"operationSequence\":9007199254740992"
                                )
                            Encoding.UTF8
                                .GetString(current)
                                .Replace(
                                    "\"expiredBefore\":null",
                                    "\"expiredBefore\":null,\"extra\":true"
                                )
                            text (String.replicate 32 "AB") "null"
                            text (String.replicate 32 "ab") "\"2026-13-09T12:00:00Z\""
                        ] do
                        Expect.isError
                            (CheckpointRecord.decode (Encoding.UTF8.GetBytes candidate))
                            "Malformed or obsolete evidence refuses")
        ]
