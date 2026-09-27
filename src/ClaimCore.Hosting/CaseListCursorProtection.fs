namespace ClaimCore.Hosting

open System
open System.Security.Cryptography
open System.Text
open ClaimCore.Application

/// A runtime-scoped key makes list positions confidential and authentic. Restart invalidates all
/// outstanding continuations; the service never persists or logs this key or plaintext token data.
type internal CaseListCursorProtection(key: byte array) =
    let secret =
        if key.Length <> 32 then
            invalidArg (nameof key) "A case-list cursor key must be 256 bits."

        Array.copy key

    let purpose = Encoding.ASCII.GetBytes("ClaimCore.CaseListCursor.1")
    let mutable disposed = false

    let encode (bytes: byte array) =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let decode (token: string) =
        if
            String.IsNullOrEmpty token
            || token.Length > 768
            || token
               |> Seq.exists (fun value ->
                   not (
                       (value >= 'A' && value <= 'Z')
                       || (value >= 'a' && value <= 'z')
                       || (value >= '0' && value <= '9')
                       || value = '-'
                       || value = '_'
                   ))
        then
            None
        else
            try
                let restored = token.Replace('-', '+').Replace('_', '/')
                let padding = String('=', (4 - restored.Length % 4) % 4)
                let bytes = Convert.FromBase64String(restored + padding)
                if encode bytes = token then Some bytes else None
            with :? FormatException ->
                None

    interface ICaseListCursorProtection with
        member _.Seal(payload) =
            if disposed || payload.Length < 79 || payload.Length > 512 then
                invalidOp "A list continuation cannot be protected."

            let nonce = RandomNumberGenerator.GetBytes 12
            let encrypted = Array.zeroCreate<byte> payload.Length
            let tag = Array.zeroCreate<byte> 16
            use aes = new AesGcm(secret, 16)
            aes.Encrypt(nonce, payload, encrypted, tag, purpose)
            Array.concat [ [| 1uy |]; nonce; encrypted; tag ] |> encode

        member _.Open(token) =
            if disposed then
                None
            else
                match decode token with
                | Some bytes when
                    bytes.Length >= 1 + 12 + 79 + 16
                    && bytes.Length <= 1 + 12 + 512 + 16
                    && bytes[0] = 1uy
                    ->
                    let encryptedLength = bytes.Length - 29
                    let plaintext = Array.zeroCreate<byte> encryptedLength
                    let nonce = bytes.AsSpan(1, 12)
                    let encrypted = bytes.AsSpan(13, encryptedLength)
                    let tag = bytes.AsSpan(13 + encryptedLength, 16)

                    try
                        use aes = new AesGcm(secret, 16)
                        aes.Decrypt(nonce, encrypted, tag, plaintext, purpose)
                        Some plaintext
                    with :? CryptographicException ->
                        CryptographicOperations.ZeroMemory plaintext
                        None
                | _ -> None

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                CryptographicOperations.ZeroMemory secret
