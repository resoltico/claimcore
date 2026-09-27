namespace ClaimCore.Database

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ClaimCore.Postgres

/// Strict canonical document and Ed25519 public-key helpers for owner-private host evidence.
module internal DatabaseIndependentHostJson =
    let sha256 (bytes: byte array) =
        SHA256.HashData(bytes) |> Convert.ToHexStringLower

    let hash (value: string) =
        value.Length = 64
        && (value |> Seq.forall (fun c -> ('0' <= c && c <= '9') || ('a' <= c && c <= 'f')))

    let text (name: string) (root: JsonElement) =
        let value = root.GetProperty(name)

        if value.ValueKind <> JsonValueKind.String then
            invalidOp "Independent host evidence has an invalid string."

        value.GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Independent host evidence has a null string.")

    let uuid (name: string) (root: JsonElement) =
        let encoded = text name root
        let value = Guid.ParseExact(encoded, "D")

        if value = Guid.Empty || encoded <> value.ToString("D") then
            invalidOp "Independent host evidence has an invalid identity."

        value

    let integer (name: string) (root: JsonElement) =
        let value = root.GetProperty(name)
        let mutable parsed = 0L

        if value.ValueKind <> JsonValueKind.Number || not (value.TryGetInt64(&parsed)) then
            invalidOp "Independent host evidence has an invalid integer."

        parsed

    let flag (name: string) (root: JsonElement) =
        match root.GetProperty(name).ValueKind with
        | JsonValueKind.True -> true
        | JsonValueKind.False -> false
        | _ -> invalidOp "Independent host evidence has an invalid Boolean."

    let instant (name: string) (root: JsonElement) =
        let encoded = text name root

        let value =
            DateTimeOffset.ParseExact(
                encoded,
                "yyyy-MM-ddTHH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
            )

        if
            value.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)
            <> encoded
        then
            invalidOp "Independent host evidence has a noncanonical time."

        value

    let exact (names: string list) (root: JsonElement) =
        if not (DatabaseRestoreCanonical.exactProperties names root) then
            invalidOp "Independent host evidence has an unexpected field."

    let digest (name: string) (root: JsonElement) =
        let value = text name root

        if not (hash value) then
            invalidOp "Independent host evidence has an invalid digest."

        value

    let canonical (maximum: int) (bytes: byte array) =
        if isNull (box bytes) || bytes.Length < 2 || bytes.Length > maximum then
            invalidOp "Independent host evidence size is invalid."

        DatabaseRestoreCanonical.parse bytes
        |> Option.defaultWith (fun () -> invalidOp "Independent host evidence is noncanonical.")

    let signed maximum (key: byte array) (bytes: byte array) (signature: byte array) =
        if
            isNull (box key)
            || key.Length <> 32
            || isNull (box signature)
            || signature.Length <> 64
            || not (ManagedCopySignature.verify key bytes signature)
        then
            invalidOp "Independent host evidence signature is invalid."

        canonical maximum bytes

    let rawPublicKey (pem: byte array) =
        if isNull (box pem) || pem.Length < 64 || pem.Length > 512 then
            invalidOp "Independent host public key is invalid."

        let source = Encoding.ASCII.GetString(pem)
        let beginMarker = "-----BEGIN PUBLIC KEY-----\n"
        let endMarker = "-----END PUBLIC KEY-----\n"

        if
            not (source.StartsWith(beginMarker, StringComparison.Ordinal))
            || not (source.EndsWith(endMarker, StringComparison.Ordinal))
        then
            invalidOp "Independent host public key encoding is invalid."

        let encoded =
            source
                .Substring(
                    beginMarker.Length,
                    source.Length - beginMarker.Length - endMarker.Length
                )
                .Replace("\n", "", StringComparison.Ordinal)

        let der = Convert.FromBase64String(encoded)
        let prefix = Convert.FromHexString("302a300506032b6570032100")

        if der.Length <> 44 || not (der.AsSpan(0, prefix.Length).SequenceEqual(prefix)) then
            invalidOp "Independent host public key is not Ed25519."

        der[prefix.Length ..]
