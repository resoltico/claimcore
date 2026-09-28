namespace ClaimCore.Postgres

open System
open System.Globalization
open System.Text.Json

/// The private-file proof expiry is bound inside the owner canonical authority event. Exact
/// re-encoding by the full audit rejects any alternate field set or representation.
module internal ManagedCopyExternalPublicationCodec =
    let privateExpiry (canonical: byte array) =
        if isNull (box canonical) || canonical.Length < 2 || canonical.Length > 16384 then
            None
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(canonical))

                let raw =
                    document.RootElement.GetProperty("privateLocationExpiresAt").GetString()
                    |> Option.ofObj
                    |> Option.defaultWith (fun () -> invalidOp "Publication expiry is null.")

                let instant = DateTimeOffset.ParseExact(raw, "O", CultureInfo.InvariantCulture)

                if instant.Offset = TimeSpan.Zero && instant.ToString("O") = raw then
                    Some instant
                else
                    None
            with _ ->
                None
