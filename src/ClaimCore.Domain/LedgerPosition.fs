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

/// Independently retained operator evidence couples an accepted prefix with confirmed expiry.
type InstallationCheckpoint =
    private
        {
            InstallationValue: System.Guid
            PositionValue: LedgerPosition
            ExpiredBeforeValue: System.DateTimeOffset option
        }

    member this.InstallationId = this.InstallationValue
    member this.Position = this.PositionValue
    member this.ExpiredBefore = this.ExpiredBeforeValue

module InstallationCheckpoint =
    let create installation position expiredBefore =
        if installation = System.Guid.Empty then
            None
        else
            Some
                {
                    InstallationValue = installation
                    PositionValue = position
                    ExpiredBeforeValue =
                        expiredBefore
                        |> Option.map (fun (instant: System.DateTimeOffset) ->
                            instant.ToUniversalTime())
                }

/// Provenance is an operation identity plus its ordered effect ordinal.
[<Struct>]
type EffectId = private EffectId of OperationId * int

module EffectId =
    let create operation index =
        if index < 0 then None else Some(EffectId(operation, index))

    let operation (EffectId(operation, _)) = operation
    let index (EffectId(_, index)) = index

/// Ordering never conflates different effects of one accepted operation.
[<Struct>]
type EffectPosition = private EffectPosition of Sequence * int

module EffectPosition =
    let create sequence index =
        if sequence < Sequence.first || index < 0 then
            None
        else
            Some(EffectPosition(sequence, index))

    let sequence (EffectPosition(sequence, _)) = sequence
    let index (EffectPosition(_, index)) = index

    /// Canonical technical position text; both components are necessary to identify an effect.
    let format position =
        Sequence.format (sequence position)
        + "/"
        + (index position).ToString(System.Globalization.CultureInfo.InvariantCulture)
