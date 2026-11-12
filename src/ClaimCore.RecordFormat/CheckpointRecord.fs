namespace ClaimCore.RecordFormat

open System
open System.Globalization
open ClaimCore.Domain
open ClaimCore.Json

/// Operator custody supplies trust; this closed format only admits the evidence's structure.
module CheckpointRecord =
    open Codec

    [<Literal>]
    let MaximumBytes = 4096

    let private frontierFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"

    let formatFrontier (value: DateTimeOffset) =
        value.ToUniversalTime().ToString(frontierFormat, CultureInfo.InvariantCulture)

    /// Operational cutoffs preserve all ticks, independently of microsecond ledger recording time.
    let frontierInstant =

        patternedText
            28
            28
            "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{7}Z$"
            (Some "date-time")
        |> interpret
            (fun path raw ->
                match
                    DateTimeOffset.TryParseExact(
                        raw,
                        frontierFormat,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
                    )
                with
                | true, value -> Ok value
                | _ ->
                    Error
                        {
                            Path = path
                            Reason = "Invalid precise UTC cutoff."
                        })
            formatFrontier

    let private hash =
        patternedText 64 64 "^[0-9a-f]{64}$" None
        |> map
            (Convert.FromHexString >> EntryHash.ofBytes >> Option.get)
            (EntryHash.toBytes >> Convert.ToHexStringLower)

    let codec =
        (record (fun installation sequence hash frontier ->
            InstallationCheckpoint.create
                installation
                (LedgerPosition.create sequence hash)
                frontier
            |> Option.map Ok
            |> Option.defaultValue (Error()))
        : Fields<InstallationCheckpoint, _>)
        |> field "installationId" _.InstallationId uuid
        |> field
            "operationSequence"
            (fun checkpoint -> checkpoint.Position.Sequence)
            (integer 0L SafeInteger.Maximum
             |> map (Sequence.create >> Option.get) Sequence.value)
        |> field "operationHash" (fun checkpoint -> checkpoint.Position.EntryHash) hash
        |> field "expiredBefore" _.ExpiredBefore (nullable frontierInstant)
        |> buildResult (fun path () ->
            {
                Path = path
                Reason = "Invalid installation checkpoint."
            })
        |> named "InstallationCheckpoint"

    let encode checkpoint =
        codec.Write checkpoint |> CanonicalWriter.bytes

    let decode bytes = Codec.decode MaximumBytes codec bytes
