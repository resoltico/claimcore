namespace ClaimCore.RecordFormat

open System
open System.Security.Cryptography
open System.Text
open ClaimCore.Domain

/// A custom UUIDv8 binds the immutable installation to the case's folded ASCII reference.
module CaseReferenceId =
    let encode installationId reference =
        if
            installationId = Guid.Empty
            || (CaseReference.parse
                >> Result.map ignore
                >> Result.mapError (fun violation ->
                    Refusal.invalidInput (InputTarget.Field BusinessField.CaseReference, violation)))
                reference
               |> Result.isError
        then
            invalidArg (nameof reference) "Use a nonempty installation and valid case reference."

        let prefix =
            Encoding.ASCII.GetBytes(
                sprintf "claimcore/case-reference-id/%d\n" RecordVersions.CaseReferenceIdentity
            )

        let key = CaseReference.key reference |> Encoding.ASCII.GetBytes

        let digest =
            SHA256.HashData(
                Array.concat [ prefix; installationId.ToByteArray(bigEndian = true); key ]
            )

        let bytes = digest[0..15]
        bytes[6] <- (bytes[6] &&& 0x0Fuy) ||| 0x80uy
        bytes[8] <- (bytes[8] &&& 0x3Fuy) ||| 0x80uy
        Guid(ReadOnlySpan<byte>(bytes), bigEndian = true) |> CaseId.create |> Option.get
