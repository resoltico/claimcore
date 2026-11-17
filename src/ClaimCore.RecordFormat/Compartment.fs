namespace ClaimCore.RecordFormat

open System
open System.Buffers.Binary
open System.Security.Cryptography
open System.Text
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Json

[<RequireQualifiedAccess>]
type CompartmentProblem =
    | InvalidOrdinal
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

    let i64 value =
        let bytes = Array.zeroCreate<byte> 8
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(), value)
        bytes

    let frame (bytes: byte array) =
        Array.append (u32 (uint32 bytes.Length)) bytes

    let text (value: string) = Encoding.UTF8.GetBytes value |> frame
    let uuid (value: Guid) = value.ToByteArray(true)

type CompartmentDescriptor =
    private
        {
            OrdinalValue: int
            OwnerValue: CompartmentOwner
        }

    member this.Ordinal = this.OrdinalValue
    member this.Owner = this.OwnerValue

module CompartmentDescriptor =
    let create ordinal owner =
        if ordinal < 0 || ordinal > 1 then
            Error CompartmentProblem.InvalidOrdinal
        else
            Ok
                {
                    OrdinalValue = ordinal
                    OwnerValue = owner
                }

    let value (descriptor: CompartmentDescriptor) =
        JsonValue.Object
            [
                "ordinal", JsonValue.Integer(int64 descriptor.Ordinal)
                "owner", PartCodecs.owner.Write descriptor.Owner
            ]

    let internal bytes (descriptor: CompartmentDescriptor) =
        value descriptor |> CanonicalWriter.bytes

/// The commitment and owner survive erasure; available arrays never escape by alias.
type Compartment =
    private
        {
            DescriptorValue: CompartmentDescriptor
            CommitmentValue: byte array
            ContentValue: (byte array * byte array) option
        }

    member this.Descriptor = this.DescriptorValue
    member this.Commitment = Array.copy this.CommitmentValue

module Compartment =
    [<Literal>]
    let MaximumContentBytes = 32768

    let private validBytes length (bytes: byte array) =
        not (obj.ReferenceEquals(bytes, null)) && bytes.Length = length

    let private validate nonce (payload: byte array) =
        if not (validBytes 32 nonce) then
            Error CompartmentProblem.InvalidNonce
        elif
            obj.ReferenceEquals(payload, null)
            || payload.Length = 0
            || payload.Length > MaximumContentBytes
        then
            Error CompartmentProblem.InvalidContent
        else
            Ok()

    let private hash nonce payload =
        Array.append nonce payload |> SHA256.HashData

    let seal descriptor nonce payload =
        validate nonce payload
        |> Result.map (fun () ->
            let nonce, payload = Array.copy nonce, Array.copy payload

            {
                DescriptorValue = descriptor
                CommitmentValue = hash nonce payload
                ContentValue = Some(nonce, payload)
            })

    let restore descriptor commitment nonce payload =
        if not (validBytes 32 commitment) then
            Error CompartmentProblem.InvalidCommitment
        else
            match nonce, payload with
            | None, None ->
                Ok
                    {
                        DescriptorValue = descriptor
                        CommitmentValue = Array.copy commitment
                        ContentValue = None
                    }
            | Some nonce, Some payload ->
                seal descriptor nonce payload
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
        |> Option.map (fun (nonce, payload) -> Array.copy nonce, Array.copy payload)

    let withoutContent value = { value with ContentValue = None }

    let distinctAvailableNonces values =
        let seen = Collections.Generic.HashSet<string>(StringComparer.Ordinal)

        values
        |> Seq.forall (fun value ->
            value.ContentValue
            |> Option.forall (fun (nonce, _) -> seen.Add(Convert.ToHexString nonce)))
