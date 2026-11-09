namespace ClaimCore.RecordFormat

open System
open System.Buffers.Binary
open System.Security.Cryptography
open System.Text
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type CompartmentOwner =
    | Case of CaseId
    | ActorProfile of ActorId

[<RequireQualifiedAccess>]
type CompartmentPurpose =
    | Case
    | Work
    | Note
    | Reminder
    | Profile

[<RequireQualifiedAccess>]
type CompartmentProblem =
    | InvalidInstallation
    | InvalidOrdinal
    | InvalidOwnerPurpose
    | InvalidEffectReferences
    | InvalidNonce
    | InvalidContent
    | InvalidCommitment
    | IncompleteContent
    | CommitmentMismatch

/// Lengths and integers are unsigned big-endian; UUID bytes use RFC 4122 order.
module internal OperationFrames =
    let u32 value =
        let bytes = Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(), value)
        bytes

    let frame (bytes: byte array) =
        Array.append (u32 (uint32 bytes.Length)) bytes

    let text value =
        Encoding.UTF8.GetBytes(value: string) |> frame

    let uuid (value: Guid) = value.ToByteArray(true)

/// References identify ordered effects in the same operation. The command grammar checks closure.
type CompartmentDescriptor =
    private
        {
            OrdinalValue: int
            OwnerValue: CompartmentOwner
            PurposeValue: CompartmentPurpose
            EffectsValue: int list
        }

    member this.Ordinal = this.OrdinalValue
    member this.Owner = this.OwnerValue
    member this.Purpose = this.PurposeValue
    member this.Effects = this.EffectsValue

module CompartmentDescriptor =
    let create ordinal owner purpose effects =
        let ownerMatches =
            match owner, purpose with
            | CompartmentOwner.ActorProfile _, CompartmentPurpose.Profile -> true
            | CompartmentOwner.Case _,
              (CompartmentPurpose.Case | CompartmentPurpose.Work | CompartmentPurpose.Note | CompartmentPurpose.Reminder) ->
                true
            | _ -> false

        let maximumReferences = if purpose = CompartmentPurpose.Reminder then 2 else 1

        if ordinal < 0 then
            Error CompartmentProblem.InvalidOrdinal
        elif not ownerMatches then
            Error CompartmentProblem.InvalidOwnerPurpose
        elif
            List.isEmpty effects
            || List.length effects > maximumReferences
            || List.exists (fun index -> index < 0) effects
            || not (List.pairwise effects |> List.forall (fun (left, right) -> left < right))
        then
            Error CompartmentProblem.InvalidEffectReferences
        else
            Ok
                {
                    OrdinalValue = ordinal
                    OwnerValue = owner
                    PurposeValue = purpose
                    EffectsValue = effects
                }

    let internal bytes (value: CompartmentDescriptor) =
        let ownerKind, ownerId =
            match value.Owner with
            | CompartmentOwner.Case id -> 1uy, CaseId.value id
            | CompartmentOwner.ActorProfile id -> 2uy, ActorId.value id

        let purpose =
            match value.Purpose with
            | CompartmentPurpose.Case -> 1uy
            | CompartmentPurpose.Work -> 2uy
            | CompartmentPurpose.Note -> 3uy
            | CompartmentPurpose.Reminder -> 4uy
            | CompartmentPurpose.Profile -> 5uy

        Array.concat
            [
                [| ownerKind |]
                OperationFrames.uuid ownerId
                [| purpose |]
                OperationFrames.u32 (uint32 value.Ordinal)
                OperationFrames.u32 (uint32 value.Effects.Length)
                value.Effects |> List.map (uint32 >> OperationFrames.u32) |> Array.concat
            ]

/// The retained commitment and owner survive erasure. Available byte arrays never escape by alias.
type Compartment =
    private
        {
            InstallationValue: Guid
            OperationValue: OperationId
            DescriptorValue: CompartmentDescriptor
            CommitmentValue: byte array
            ContentValue: (byte array * byte array) option
        }

    member this.InstallationId = this.InstallationValue
    member this.OperationId = this.OperationValue
    member this.Descriptor = this.DescriptorValue
    member this.Commitment = Array.copy this.CommitmentValue

module Compartment =
    [<Literal>]
    let MaximumContentBytes = 32768

    let private validBytes length (value: byte array) =
        not (obj.ReferenceEquals(value, null)) && value.Length = length

    let private contentValid (nonce: byte array) (bytes: byte array) =
        if not (validBytes 32 nonce) then
            Error CompartmentProblem.InvalidNonce
        elif
            obj.ReferenceEquals(bytes, null)
            || bytes.Length = 0
            || bytes.Length > MaximumContentBytes
        then
            Error CompartmentProblem.InvalidContent
        else
            Ok()

    let private hash installation operation descriptor nonce bytes =
        Array.concat
            [
                OperationFrames.text "ClaimCore.Compartment"
                OperationFrames.u32 (uint32 InstallationFormat.Current)
                OperationFrames.uuid installation
                OperationFrames.uuid (OperationId.value operation)
                CompartmentDescriptor.bytes descriptor
                nonce
                OperationFrames.frame bytes
            ]
        |> SHA256.HashData

    /// Bind exact bytes; the typed retained decoder owns their canonical/semantic interpretation.
    /// The trusted append adapter supplies fresh CSPRNG bytes; command input never owns a nonce.
    let seal installation operation descriptor nonce bytes =
        if installation = Guid.Empty then
            Error CompartmentProblem.InvalidInstallation
        else
            contentValid nonce bytes
            |> Result.map (fun () ->
                let nonce, bytes = Array.copy nonce, Array.copy bytes

                {
                    InstallationValue = installation
                    OperationValue = operation
                    DescriptorValue = descriptor
                    CommitmentValue = hash installation operation descriptor nonce bytes
                    ContentValue = Some(nonce, bytes)
                })

    /// An erased compartment cannot prove its former contents or nonce freshness.
    let restore installation operation descriptor commitment nonce bytes =
        if installation = Guid.Empty then
            Error CompartmentProblem.InvalidInstallation
        elif not (validBytes 32 commitment) then
            Error CompartmentProblem.InvalidCommitment
        else
            match nonce, bytes with
            | None, None ->
                Ok
                    {
                        InstallationValue = installation
                        OperationValue = operation
                        DescriptorValue = descriptor
                        CommitmentValue = Array.copy commitment
                        ContentValue = None
                    }
            | Some nonce, Some bytes ->
                seal installation operation descriptor nonce bytes
                |> Result.bind (fun value ->
                    if
                        CryptographicOperations.FixedTimeEquals(value.CommitmentValue, commitment)
                    then
                        Ok value
                    else
                        Error CompartmentProblem.CommitmentMismatch)
            | _ -> Error CompartmentProblem.IncompleteContent

    let content value =
        value.ContentValue
        |> Option.map (fun (nonce, bytes) -> Array.copy nonce, Array.copy bytes)

    let withoutContent value = { value with ContentValue = None }

    /// Checks only the supplied available scope; erased nonces are deliberately not recoverable.
    let distinctAvailableNonces compartments =
        let observed = Collections.Generic.HashSet<string>(StringComparer.Ordinal)

        compartments
        |> Seq.forall (fun value ->
            match value.ContentValue with
            | None -> true
            | Some(nonce, _) -> observed.Add(Convert.ToHexString nonce))
