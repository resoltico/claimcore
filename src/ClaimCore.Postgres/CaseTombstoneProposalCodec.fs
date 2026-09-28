namespace ClaimCore.Postgres

open System
open System.Globalization
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.Application

/// Exact stored proposal decoding for read-only audit; malformed alternatives never become
/// authority simply because they contain a familiar kind token.
module internal CaseTombstoneProposalCodec =
    let private names =
        [|
            "version"
            "kind"
            "eventId"
            "caseId"
            "purgeEventId"
            "purgeWitnessSequence"
            "purgeWitnessEpoch"
            "purgeWitnessHash"
            "cutoffSequence"
            "cutoffHash"
            "targetCount"
            "targetDigest"
            "expectedAuthorityRevision"
            "expectedAuthorityHash"
            "validUntil"
        |]

    let private text (root: JsonElement) (name: string) =
        root.GetProperty(name).GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Tombstone proposal text is null.")

    let private instant (root: JsonElement) (name: string) =
        DateTimeOffset.ParseExact(
            text root name,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None
        )

    let decode (canonical: byte array) : TombstonePruneProposal option =
        if canonical.Length < 2 || canonical.Length > 8192 then
            None
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(canonical))
                let root = document.RootElement
                let actual = root.EnumerateObject() |> Seq.map _.Name |> Seq.toArray

                if
                    actual <> names
                    || root.GetProperty("version").GetInt32() <> 1
                    || root.GetProperty("kind").GetString() <> "CASE_WITNESS_PRUNE_PROPOSAL"
                then
                    None
                else
                    let value =
                        {
                            EventId = root.GetProperty("eventId").GetGuid()
                            CaseId = root.GetProperty("caseId").GetGuid()
                            PurgeEventId = root.GetProperty("purgeEventId").GetGuid()
                            PurgeWitnessSequence =
                                root.GetProperty("purgeWitnessSequence").GetInt64()
                            PurgeWitnessEpoch = root.GetProperty("purgeWitnessEpoch").GetInt64()
                            PurgeWitnessHash = text root "purgeWitnessHash"
                            CutoffSequence = root.GetProperty("cutoffSequence").GetInt64()
                            CutoffHash = text root "cutoffHash"
                            TargetCount = root.GetProperty("targetCount").GetInt64()
                            TargetDigest = text root "targetDigest"
                            ExpectedAuthorityRevision =
                                root.GetProperty("expectedAuthorityRevision").GetInt64()
                            ExpectedAuthorityHash = text root "expectedAuthorityHash"
                            ValidUntil = instant root "validUntil"
                        }

                    let rebuilt = CaseTombstoneCandidate.proposal value

                    try
                        if rebuilt = canonical then Some value else None
                    finally
                        CryptographicOperations.ZeroMemory(rebuilt)
            with _ ->
                None
