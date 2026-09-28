namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Text
open ClaimCore.Application

/// Private input IDs are reduced to installation-keyed commitments before durable storage.
/// An unknown set is represented by a terminal installation fence, never by an empty known list.
module internal InstallationLossOperationCommitments =
    let private prefix = Encoding.ASCII.GetBytes("CLAIMCORE_LOSS_DENIAL_SET_V1\000")

    let private emptyDigest = SHA256.HashData(prefix)

    let digestOfCommitments (commitments: seq<byte array>) =
        use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        hash.AppendData(prefix)

        for value in commitments do
            if value.Length <> 32 then
                invalidOp "Loss operation commitment is invalid."

            hash.AppendData(value)

        hash.GetHashAndReset()

    let private strictIds (bytes: byte array) =
        if isNull (box bytes) || bytes.Length > 370000 then
            invalidOp "Loss operation list exceeds its private input bound."

        if bytes.Length = 0 then
            []
        else
            let source = UTF8Encoding(false, true).GetString(bytes)

            if not (source.EndsWith('\n')) || source.Contains('\r') then
                invalidOp "Loss operation list is not canonical."

            let tokens = source.Split('\n')
            let ids = tokens[.. tokens.Length - 2] |> Array.toList

            if
                ids.Length > 10000
                || ids
                   |> List.exists (fun raw ->
                       match Guid.TryParseExact(raw, "D") with
                       | true, value -> value = Guid.Empty || raw <> value.ToString("D")
                       | _ -> true)
                || ids
                   <> (ids
                       |> List.distinct
                       |> List.sortWith (fun a b -> StringComparer.Ordinal.Compare(a, b)))
            then
                invalidOp "Loss operation IDs are not a sorted unique canonical list."

            ids |> List.map (fun raw -> Guid.ParseExact(raw, "D"))

    let commitments (suppression: ISuppressionCommitments) mode bytes =
        let ids = strictIds bytes

        if mode = InstallationLossOperationSet.Unknown && not ids.IsEmpty then
            invalidOp "Unknown loss operation set cannot contain individual identities."

        suppression.Admit()

        let values =
            ids
            |> List.map (fun id -> suppression.Operation id)
            |> List.sortWith (fun left right -> left.AsSpan().SequenceCompareTo(right.AsSpan()))

        if values |> List.exists (fun value -> value.Length <> 32) then
            invalidOp "Loss operation commitment is invalid."

        let digest = digestOfCommitments values

        if ids.IsEmpty && digest <> emptyDigest then
            invalidOp "Loss operation digest is invalid."

        ids.Length, digest, values
