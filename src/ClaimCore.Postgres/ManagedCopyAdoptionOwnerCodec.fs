namespace ClaimCore.Postgres

open System
open System.Text.Json

/// Only the historical case authority tip is not projected into separate adoption columns.
/// Full proof re-encodes every field and compares exact bytes before trusting this pair.
module internal ManagedCopyAdoptionOwnerCodec =
    let authorityTip (canonical: byte array) =
        if isNull (box canonical) || canonical.Length < 2 || canonical.Length > 16384 then
            None
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(canonical))
                let root = document.RootElement
                let revision = root.GetProperty("caseAuthorityRevision").GetInt64()

                let hash =
                    root.GetProperty("caseAuthorityHash").GetString()
                    |> Option.ofObj
                    |> Option.map Convert.FromHexString

                let privateExpiry =
                    root.GetProperty("privateLocationExpiresAt").GetString()
                    |> Option.ofObj
                    |> Option.map (fun raw ->
                        DateTimeOffset.ParseExact(
                            raw,
                            "O",
                            Globalization.CultureInfo.InvariantCulture
                        ))

                match hash, privateExpiry with
                | Some value, Some expiry when
                    revision >= 0L && value.Length = 32 && expiry.Offset = TimeSpan.Zero
                    ->
                    Some(revision, value, expiry)
                | _ -> None
            with _ ->
                None
