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
        let bytes = Array.zeroCreate<byte> 58

        if cursor.ActorId = Guid.Empty || cursor.GrantRevision < 1L then
            invalidArg (nameof cursor) "Recovery cursor authority is invalid."

        bytes[0] <- 1uy

        bytes[1] <-
            match cursor.View with
            | RecoveryListView.Pending -> 0uy
            | RecoveryListView.Terminal -> 1uy

        BinaryPrimitives.WriteInt64BigEndian(
            bytes.AsSpan(2, 8),
            cursor.OccurredAt.UtcDateTime.Ticks
        )

        CursorIdentity.write cursor.OperationId (bytes.AsSpan(10, 16))
        CursorIdentity.write cursor.ActorId (bytes.AsSpan(26, 16))
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(42, 8), cursor.GrantRevision)

        BinaryPrimitives.WriteInt64BigEndian(
            bytes.AsSpan(50, 8),
            cursor.ExpiresAt.UtcDateTime.Ticks
        )

        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let decode (token: string) =
        let restored = token.Replace('-', '+').Replace('_', '/')

        if restored.Length <> 78 then
            Error "Use one opaque recovery cursor returned by ClaimCore."
        else
            try
                let bytes = Convert.FromBase64String(restored + "==")

                if bytes.Length <> 58 || bytes[0] <> 1uy then
                    Error "Use one opaque recovery cursor returned by ClaimCore."
                else
                    let operationId = Guid(bytes.AsSpan(10, 16))
                    let actorId = Guid(bytes.AsSpan(26, 16))
                    let grantRevision = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(42, 8))

                    let view =
                        match bytes[1] with
                        | 0uy -> Some RecoveryListView.Pending
                        | 1uy -> Some RecoveryListView.Terminal
                        | _ -> None

                    if
                        operationId = Guid.Empty
                        || actorId = Guid.Empty
                        || grantRevision < 1L
                        || view.IsNone
                    then
                        Error "Use one opaque recovery cursor returned by ClaimCore."
                    else
                        let ticks = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(2, 8))
                        let expiryTicks = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(50, 8))

                        try
                            Ok
                                {
                                    View = view.Value
                                    OccurredAt = DateTimeOffset(DateTime(ticks, DateTimeKind.Utc))
                                    OperationId = operationId
                                    ActorId = actorId
                                    GrantRevision = grantRevision
                                    ExpiresAt =
                                        DateTimeOffset(DateTime(expiryTicks, DateTimeKind.Utc))
                                }
                        with :? ArgumentOutOfRangeException ->
                            Error "Use one opaque recovery cursor returned by ClaimCore."
            with :? FormatException ->
                Error "Use one opaque recovery cursor returned by ClaimCore."
