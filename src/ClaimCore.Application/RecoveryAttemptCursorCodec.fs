namespace ClaimCore.Application

open System
open System.Buffers.Binary

/// Opaque keyset cursor binds detailed attempt paging to one operation identity.
module internal RecoveryAttemptCursorCodec =
    let encode (cursor: RecoveryAttemptCursor) =
        let bytes = Array.zeroCreate<byte> 40
        cursor.OperationId.TryWriteBytes(bytes.AsSpan(0, 16)) |> ignore

        BinaryPrimitives.WriteInt64BigEndian(
            bytes.AsSpan(16, 8),
            cursor.StartedAt.UtcDateTime.Ticks
        )

        cursor.AttemptId.TryWriteBytes(bytes.AsSpan(24, 16)) |> ignore
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let decode (token: string) =
        let restored = token.Replace('-', '+').Replace('_', '/')

        if restored.Length <> 54 then
            Error "Use one opaque recovery attempt cursor returned by ClaimCore."
        else
            try
                let bytes = Convert.FromBase64String(restored + "==")

                if bytes.Length <> 40 then
                    Error "Use one opaque recovery attempt cursor returned by ClaimCore."
                else
                    let operationId = Guid(bytes.AsSpan(0, 16))
                    let attemptId = Guid(bytes.AsSpan(24, 16))
                    let ticks = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(16, 8))

                    if operationId = Guid.Empty || attemptId = Guid.Empty then
                        Error "Use one opaque recovery attempt cursor returned by ClaimCore."
                    else
                        try
                            Ok
                                {
                                    OperationId = operationId
                                    StartedAt = DateTimeOffset(DateTime(ticks, DateTimeKind.Utc))
                                    AttemptId = attemptId
                                }
                        with :? ArgumentOutOfRangeException ->
                            Error "Use one opaque recovery attempt cursor returned by ClaimCore."
            with :? FormatException ->
                Error "Use one opaque recovery attempt cursor returned by ClaimCore."
