namespace ClaimCore.Application

open System
open System.Buffers.Binary

/// Encodes the storage revision continuation as an opaque fixed-width token. The representation is
/// deliberately private to Application so neither transport treats historical revision as a query
/// parameter with independent meaning.
module internal HistoryCursor =
    let encode version =
        let bytes = Array.zeroCreate<byte> 8
        BinaryPrimitives.WriteInt64BigEndian(bytes, version)

        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let decode (cursor: string) =
        let restored = cursor.Replace('-', '+').Replace('_', '/')

        if restored.Length <> 11 then
            Error()
        else
            try
                let bytes = Convert.FromBase64String(restored + "=")

                if bytes.Length <> 8 then
                    Error()
                else
                    let version = BinaryPrimitives.ReadInt64BigEndian(bytes)

                    if version < 0L then Error() else Ok version
            with :? FormatException ->
                Error()
