namespace ClaimCore.Postgres

open System
open System.Buffers.Binary
open System.Security.Cryptography
open System.Text

/// Domain-separated copy history hash. Exact attestation bytes and detached signature are inputs.
module internal ManagedCopyEventHash =
    let private domain = Encoding.ASCII.GetBytes("CLAIMCORE_MANAGED_COPY_EVENT_V1\000")

    let compute (previousHash: byte[]) (canonical: byte[]) (signature: byte[] option) =
        if previousHash.Length <> 32 || canonical.Length = 0 || canonical.Length > 300000 then
            invalidArg (nameof canonical) "Managed-copy event evidence is invalid."

        let signed = signature |> Option.defaultValue Array.empty

        if signed.Length <> 0 && signed.Length <> 64 then
            invalidArg (nameof signature) "Managed-copy signature is invalid."

        let length = Array.zeroCreate<byte> 4
        use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        hash.AppendData(domain)
        hash.AppendData(previousHash)
        BinaryPrimitives.WriteInt32BigEndian(length, canonical.Length)
        hash.AppendData(length)
        hash.AppendData(canonical)
        BinaryPrimitives.WriteInt32BigEndian(length, signed.Length)
        hash.AppendData(length)
        hash.AppendData(signed)
        hash.GetHashAndReset()
