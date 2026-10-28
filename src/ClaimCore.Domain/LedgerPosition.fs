namespace ClaimCore.Domain

[<Struct>]
type EntryHash = private EntryHash of byte array

module EntryHash =
    let ofBytes (bytes: byte array) =
        if obj.ReferenceEquals(bytes, null) || bytes.Length <> 32 then
            None
        else
            Some(EntryHash(Array.copy bytes))

    let toBytes (EntryHash bytes) = Array.copy bytes
    let zero = EntryHash(Array.zeroCreate 32)

type LedgerPosition =
    {
        Sequence: int64
        EntryHash: EntryHash
    }

module LedgerPosition =
    let create sequence hash =
        if sequence < 0L || sequence > SafeInteger.Maximum then
            None
        else
            Some
                {
                    Sequence = sequence
                    EntryHash = hash
                }

    let genesis =
        {
            Sequence = 0L
            EntryHash = EntryHash.zero
        }
