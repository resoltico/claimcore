namespace ClaimCore.Application

open System
open System.Buffers.Binary
open System.Security.Cryptography
open System.Text
open ClaimCore.Domain

/// The encrypted list cursor binds a visible key to one actor, principal, grant revision and
/// exact page query. The runtime key is ephemeral, so restart invalidates outstanding cursors.
module internal CaseListCursorCodec =
    let private text = UTF8Encoding(false, true)
    let private headerLength = 79
    let private lifetime = TimeSpan.FromMinutes 15.

    let private principalDigest principal =
        let kind, issuer, stable = PrincipalKey.storageParts principal
        let segments = [ kind; issuer; stable ] |> List.map text.GetBytes
        let length = segments |> List.sumBy (fun value -> 4 + value.Length)
        let bytes = Array.zeroCreate<byte> length
        let mutable offset = 0

        for segment in segments do
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset, 4), segment.Length)
            offset <- offset + 4
            segment.CopyTo(bytes, offset)
            offset <- offset + segment.Length

        try
            SHA256.HashData bytes
        finally
            CryptographicOperations.ZeroMemory bytes
            segments |> List.iter CryptographicOperations.ZeroMemory

    let encode
        (protection: ICaseListCursorProtection)
        (binding: ActorBinding)
        revision
        limit
        (issuedAt: DateTimeOffset)
        reference
        =
        if
            binding.ActorId = Guid.Empty
            || binding.GrantRevision <> revision
            || revision < 1L
            || limit < 1
            || limit > SemanticContract.current.MaximumPageSize
            || Claim.validateReference reference <> Ok()
        then
            invalidArg (nameof reference) "The visible list position is invalid."

        let referenceBytes = text.GetBytes reference
        let bytes = Array.zeroCreate<byte>(headerLength + referenceBytes.Length)
        bytes[0] <- 1uy

        if not (binding.ActorId.TryWriteBytes(bytes.AsSpan(1, 16))) then
            invalidOp "The list actor identity did not fit its fixed destination."

        (principalDigest binding.Principal).CopyTo(bytes, 17)
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(49, 8), revision)
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(57, 4), limit)
        let issued = issuedAt.ToUniversalTime().Ticks

        if issued > DateTimeOffset.MaxValue.Ticks - lifetime.Ticks then
            invalidArg (nameof issuedAt) "The cursor clock cannot represent its expiry."

        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(61, 8), issued)
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(69, 8), issued + lifetime.Ticks)
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(77, 2), uint16 referenceBytes.Length)
        referenceBytes.CopyTo(bytes, headerLength)

        try
            protection.Seal bytes
        finally
            CryptographicOperations.ZeroMemory bytes
            CryptographicOperations.ZeroMemory referenceBytes

    let private authorityMatches (binding: ActorBinding) revision limit (bytes: byte array) =
        let actor = Guid(bytes.AsSpan(1, 16))
        let digest = principalDigest binding.Principal
        let grant = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(49, 8))
        let pageLimit = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(57, 4))

        actor = binding.ActorId
        && grant = revision
        && grant = binding.GrantRevision
        && pageLimit = limit
        && CryptographicOperations.FixedTimeEquals(bytes.AsSpan(17, 32), digest)

    let private windowMatches (now: DateTimeOffset) (bytes: byte array) =
        let issued = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(61, 8))
        let expires = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(69, 8))
        let current = now.ToUniversalTime().Ticks

        issued >= 0L
        && expires <= DateTimeOffset.MaxValue.Ticks
        && expires > issued
        && expires - issued = lifetime.Ticks
        && current >= issued
        && current < expires

    let private parse
        (binding: ActorBinding)
        revision
        limit
        (now: DateTimeOffset)
        (bytes: byte array)
        =
        if bytes.Length < headerLength || bytes[0] <> 1uy then
            Error()
        else
            let size = int (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(77, 2)))

            if
                size < 1
                || bytes.Length <> headerLength + size
                || not (authorityMatches binding revision limit bytes)
                || not (windowMatches now bytes)
            then
                Error()
            else
                try
                    let reference = text.GetString(bytes, headerLength, size)

                    if Claim.validateReference reference = Ok() then
                        Ok reference
                    else
                        Error()
                with :? DecoderFallbackException ->
                    Error()

    let decode (protection: ICaseListCursorProtection) binding revision limit now token =
        match protection.Open token with
        | Some bytes ->
            try
                parse binding revision limit now bytes
            finally
                CryptographicOperations.ZeroMemory bytes
        | None -> Error()

    let page protection binding revision limit instant (rows: Claim list) : CasePage =
        let items = rows |> List.truncate limit

        let next =
            if rows.Length > limit then
                items
                |> List.tryLast
                |> Option.map (fun claim ->
                    encode
                        protection
                        binding
                        revision
                        limit
                        instant
                        (Claim.view claim).Fields.CaseReference)
            else
                None

        { Items = items; NextCursor = next }
