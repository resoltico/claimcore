namespace ClaimCore.RecordFormat

open System
open System.Buffers.Binary
open System.Security.Cryptography
open System.Text
open ClaimCore.Domain

type CursorEnvelope =
    {
        IssuedAtTicks: int64
        Binding: byte array
        Position: string
    }

/// Authenticated, bounded cursor bytes. Application owns authority binding and lifetime policy.
module CursorRecord =
    [<Literal>]
    let MaximumTokenCharacters = 512

    let private text = UTF8Encoding(false, true)
    let private prefix = Encoding.ASCII.GetBytes "claimcore/cursor\n"
    let private headerSize = 43
    let private maximumPositionBytes = 256

    let private width expected (bytes: byte array) =
        not (obj.ReferenceEquals(bytes, null)) && bytes.Length = expected

    let private signature key bytes =
        HMACSHA256.HashData(key, Array.append prefix bytes)

    let private base64 (bytes: byte array) =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let encode key (envelope: CursorEnvelope) =
        if
            not (width 32 key)
            || not (width 32 envelope.Binding)
            || envelope.IssuedAtTicks < 0L
            || envelope.IssuedAtTicks > DateTimeOffset.MaxValue.UtcTicks
            || String.IsNullOrEmpty envelope.Position
            || not (UnicodeText.isPersisted envelope.Position)
        then
            invalidArg (nameof envelope) "Invalid cursor envelope."

        let position = text.GetBytes envelope.Position

        if position.Length > maximumPositionBytes then
            invalidArg (nameof envelope) "Cursor position exceeds its bound."

        let payload = Array.zeroCreate<byte>(headerSize + position.Length)
        payload[0] <- 1uy
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1, 8), envelope.IssuedAtTicks)
        envelope.Binding.CopyTo(payload, 9)
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(41, 2), uint16 position.Length)
        position.CopyTo(payload, headerSize)
        Array.append payload (signature key payload) |> base64

    let private alphabet character =
        (character >= 'a' && character <= 'z')
        || (character >= 'A' && character <= 'Z')
        || (character >= '0' && character <= '9')
        || character = '-'
        || character = '_'

    let private read key (bytes: byte array) =
        let refuse () =
            Error
                {
                    Field = "cursor"
                    Message = "Invalid cursor encoding."
                }

        if bytes.Length < headerSize + 33 then
            refuse ()
        else
            let payload = bytes[.. bytes.Length - 33]
            let supplied = bytes[bytes.Length - 32 ..]

            if not (CryptographicOperations.FixedTimeEquals(supplied, signature key payload)) then
                refuse ()
            elif payload[0] <> 1uy then
                refuse ()
            else
                let length = int (BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(41, 2)))
                let issued = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(1, 8))

                if
                    length < 1
                    || length > maximumPositionBytes
                    || payload.Length <> headerSize + length
                    || issued < 0L
                    || issued > DateTimeOffset.MaxValue.UtcTicks
                then
                    refuse ()
                else
                    let position = text.GetString(payload, headerSize, length)

                    if not (UnicodeText.isPersisted position) then
                        refuse ()
                    else
                        Ok
                            {
                                IssuedAtTicks = issued
                                Binding = payload[9..40]
                                Position = position
                            }

    let decode key (token: string) =
        let refuse () =
            Error
                {
                    Field = "cursor"
                    Message = "Invalid cursor encoding."
                }

        if
            not (width 32 key)
            || String.IsNullOrEmpty token
            || token.Length > MaximumTokenCharacters
            || not (token |> Seq.forall alphabet)
            || token.Length % 4 = 1
        then
            refuse ()
        else
            try
                let raw = token.Replace('-', '+').Replace('_', '/')

                let bytes =
                    Convert.FromBase64String(raw + String.replicate ((4 - raw.Length % 4) % 4) "=")

                if base64 bytes <> token then refuse () else read key bytes
            with
            | :? FormatException
            | :? DecoderFallbackException -> refuse ()
