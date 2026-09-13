namespace ClaimCore.Application

open System
open System.Buffers.Binary

/// Application-owned opaque recovery cursor. Its fixed binary payload preserves the storage sort
/// key without turning timestamp or operation ID into an adapter-defined protocol field.
module internal RecoveryCursorCodec =
    let encode (cursor: RecoveryCursor) =
        let bytes = Array.zeroCreate<byte> 24

        BinaryPrimitives.WriteInt64BigEndian(
            bytes.AsSpan(0, 8),
            cursor.PreparedAt.UtcDateTime.Ticks
        )

        cursor.OperationId.TryWriteBytes(bytes.AsSpan(8, 16)) |> ignore

        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let decode (token: string) =
        let restored = token.Replace('-', '+').Replace('_', '/')

        if restored.Length <> 32 then
            Error "Use one opaque recovery cursor returned by ClaimCore."
        else
            try
                let bytes = Convert.FromBase64String(restored)

                if bytes.Length <> 24 then
                    Error "Use one opaque recovery cursor returned by ClaimCore."
                else
                    let operationId = Guid(bytes.AsSpan(8, 16))

                    if operationId = Guid.Empty then
                        Error "Use one opaque recovery cursor returned by ClaimCore."
                    else
                        let ticks = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(0, 8))

                        try
                            Ok
                                {
                                    PreparedAt = DateTimeOffset(DateTime(ticks, DateTimeKind.Utc))
                                    OperationId = operationId
                                }
                        with :? ArgumentOutOfRangeException ->
                            Error "Use one opaque recovery cursor returned by ClaimCore."
            with :? FormatException ->
                Error "Use one opaque recovery cursor returned by ClaimCore."
