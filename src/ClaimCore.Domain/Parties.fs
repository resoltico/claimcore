namespace ClaimCore.Domain

open System
open System.Buffers.Binary
open System.Security.Cryptography

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type PartyId = private PartyId of Guid

module PartyId =
    let create value =
        if value = Guid.Empty then None else Some(PartyId value)

    let ofOperationId operation = PartyId(OperationId.value operation)
    let value (PartyId value) = value
    let format id = (value id).ToString("D")

    let derive operation ordinal =
        if ordinal < 0 then
            invalidArg (nameof ordinal) "The party ordinal must be non-negative."

        let bytes = Array.zeroCreate<byte> 20
        (OperationId.value operation).ToByteArray(true).CopyTo(bytes, 0)
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan 16, uint32 ordinal)
        let hash = SHA256.HashData bytes
        hash[6] <- (hash[6] &&& 0x0Fuy) ||| 0x80uy
        hash[8] <- (hash[8] &&& 0x3Fuy) ||| 0x80uy
        PartyId(Guid(hash.AsSpan(0, 16), true))

[<RequireQualifiedAccess>]
type PartyRole =
    | Claimant
    | Insurer
    | Representative
    | Other

module PartyRole =
    let vocabulary =
        Vocabulary.ofUnion<PartyRole> "partyRole" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type PartyDetails = private PartyDetails of string

module PartyDetails =
    let constraints: PlainTextConstraints =
        {
            MaximumScalars = 1000
            MaximumUtf8Bytes = 4096
            AllowsLineFeed = true
        }

    let format (PartyDetails value) = value

    let parse value =
        ScalarText.parse (Some constraints.MaximumUtf8Bytes) constraints.MaximumScalars value
        |> Result.map PartyDetails

    let admit (PartyDetails value) =
        ScalarText.admitPlain constraints.MaximumScalars constraints.AllowsLineFeed value
        |> Result.map PartyDetails

type PartyContent =
    private
        {
            RoleValue: PartyRole
            NameValue: PartyName option
            DetailsValue: PartyDetails option
        }

    member this.Role = this.RoleValue
    member this.Name = this.NameValue
    member this.Details = this.DetailsValue

[<RequireQualifiedAccess>]
type PartyContentMember =
    | Name
    | Details

module PartyContent =
    [<Literal>]
    let MaximumCurrent = 20

    let requiredContent =
        [ PartyContentMember.Name; PartyContentMember.Details ], Violation.PartyContentRequired

    let create role name details =
        let members, violation = requiredContent

        let supplied =
            function
            | PartyContentMember.Name -> Option.isSome name
            | PartyContentMember.Details -> Option.isSome details

        if not (List.exists supplied members) then
            Error violation
        else
            Ok
                {
                    RoleValue = role
                    NameValue = name
                    DetailsValue = details
                }

    let names role (parties: PartyContent list) =
        parties
        |> List.choose (fun party -> if party.Role = role then party.Name else None)

    let searchable (party: PartyContent) =
        (party.Name |> Option.map PartyName.format |> Option.toList)
        @ (party.Details |> Option.map PartyDetails.format |> Option.toList)
