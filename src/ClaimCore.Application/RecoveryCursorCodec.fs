namespace ClaimCore.Application

open System
open System.Buffers.Binary

/// Application-owned opaque recovery cursor. Its fixed binary payload preserves the storage sort
/// key without turning timestamp or operation ID into an adapter-defined protocol field.
/// A cursor carries operation identity. A short or misplaced destination would silently encode a
/// zero identity instead of failing, so the write is checked rather than discarded.
module internal CursorIdentity =
    let write (value: System.Guid) (destination: System.Span<byte>) =
        if not (value.TryWriteBytes destination) then
            invalidOp "A recovery cursor identity did not fit its fixed destination."

module internal RecoveryCursorCodec =
    let encode (cursor: RecoveryCursor) =
        let bytes = Array.zeroCreate<byte> 25

        bytes[0] <-
            match cursor.View with
            | RecoveryListView.Pending -> 0uy
            | RecoveryListView.Terminal -> 1uy

        BinaryPrimitives.WriteInt64BigEndian(
            bytes.AsSpan(1, 8),
            cursor.OccurredAt.UtcDateTime.Ticks
        )

        CursorIdentity.write cursor.OperationId (bytes.AsSpan(9, 16))

        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let decode (token: string) =
        let restored = token.Replace('-', '+').Replace('_', '/')

        if restored.Length <> 34 then
            Error "Use one opaque recovery cursor returned by ClaimCore."
        else
            try
                let bytes = Convert.FromBase64String(restored + "==")

                if bytes.Length <> 25 then
                    Error "Use one opaque recovery cursor returned by ClaimCore."
                else
                    let operationId = Guid(bytes.AsSpan(9, 16))

                    let view =
                        match bytes[0] with
                        | 0uy -> Some RecoveryListView.Pending
                        | 1uy -> Some RecoveryListView.Terminal
                        | _ -> None

                    if operationId = Guid.Empty || view.IsNone then
                        Error "Use one opaque recovery cursor returned by ClaimCore."
                    else
                        let ticks = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1, 8))

                        try
                            Ok
                                {
                                    View = view.Value
                                    OccurredAt = DateTimeOffset(DateTime(ticks, DateTimeKind.Utc))
                                    OperationId = operationId
                                }
                        with :? ArgumentOutOfRangeException ->
                            Error "Use one opaque recovery cursor returned by ClaimCore."
            with :? FormatException ->
                Error "Use one opaque recovery cursor returned by ClaimCore."
