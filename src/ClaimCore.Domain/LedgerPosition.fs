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
        Sequence: Sequence
        EntryHash: EntryHash
    }

module LedgerPosition =
    let create sequence hash =
        {
            Sequence = sequence
            EntryHash = hash
        }

    let genesis =
        {
            Sequence = Sequence.zero
            EntryHash = EntryHash.zero
        }
