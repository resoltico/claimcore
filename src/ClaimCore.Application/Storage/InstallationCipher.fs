namespace ClaimCore.Application

open System
open System.Security.Cryptography

/// A wrapping key admitted from a private file at a host boundary.
[<Sealed>]
type MasterKey internal (bytes: byte array) =
    member internal _.Bytes = bytes

    interface IDisposable with
        member _.Dispose() =
            CryptographicOperations.ZeroMemory bytes

module MasterKey =
    [<Literal>]
    let Size = 32

    let admit (bytes: byte array) =
        if bytes.Length = Size then
            Some(new MasterKey(Array.copy bytes))
        else
            None

namespace ClaimCore.Application.Storage

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open ClaimCore.Application
open ClaimCore.Application.Contracts

module private CipherEnvelope =
    let ivBytes = 12
    let tagBytes = 16
    let overhead = ivBytes + tagBytes

    let seal (key: byte array) (aad: byte array) (plain: byte array) =
        let envelope = Array.zeroCreate<byte>(plain.Length + overhead)
        RandomNumberGenerator.Fill(envelope.AsSpan(0, ivBytes))
        use aes = new AesGcm(key, tagBytes)

        aes.Encrypt(
            ReadOnlySpan<byte>(envelope, 0, ivBytes),
            ReadOnlySpan<byte> plain,
            envelope.AsSpan(ivBytes, plain.Length),
            envelope.AsSpan(ivBytes + plain.Length, tagBytes),
            ReadOnlySpan<byte> aad
        )

        envelope

    let openEnvelope (key: byte array) (aad: byte array) (envelope: byte array) =
        if envelope.Length < overhead then
            raise (CryptographicException "Invalid data envelope size.")

        let length = envelope.Length - overhead
        let plain = Array.zeroCreate<byte> length
        use aes = new AesGcm(key, tagBytes)

        aes.Decrypt(
            ReadOnlySpan<byte>(envelope, 0, ivBytes),
            ReadOnlySpan<byte>(envelope, ivBytes, length),
            ReadOnlySpan<byte>(envelope, ivBytes + length, tagBytes),
            plain.AsSpan(),
            ReadOnlySpan<byte> aad
        )

        plain

    // All authenticated fields are invariant numbers, GUIDs, and fixed structural tokens.
    let associatedData label (installationId: Guid) fields =
        String.concat "\u001f" ("claimcore" :: label :: installationId.ToString("D") :: fields)
        |> Encoding.UTF8.GetBytes

/// One admitted installation key and its independent equality keys. Ciphertext never enters RecordFormat.
[<Sealed>]
type internal InstallationCipher(installationId: Guid, key: byte array) =
    let derive label =
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            key,
            MasterKey.Size,
            Array.empty,
            Encoding.UTF8.GetBytes("claimcore/" + label)
        )

    let attachmentKey = derive "attachment-digest"
    let saltKey = derive "compartment-salt-fingerprint"

    let blob label (caseId: Guid) (blobId: Guid) =
        CipherEnvelope.associatedData
            label
            installationId
            [ caseId.ToString("D"); blobId.ToString("D") ]

    let compartment label (sequence: int64) (ordinal: int) ownerKind (ownerId: Guid) =
        CipherEnvelope.associatedData
            label
            installationId
            [
                sequence.ToString(CultureInfo.InvariantCulture)
                ordinal.ToString(CultureInfo.InvariantCulture)
                ownerKind
                ownerId.ToString("D")
            ]

    let decrypt aad payload =
        try
            CipherEnvelope.openEnvelope key aad payload
        with :? CryptographicException ->
            raise (RecordCorruptException "Stored data authentication failed.")

    member _.InstallationId = installationId

    member _.Wrap(master: MasterKey) =
        CipherEnvelope.seal
            master.Bytes
            (CipherEnvelope.associatedData "data-key" installationId [])
            key

    member _.EncryptBlob(caseId, blobId, payload) =
        CipherEnvelope.seal key (blob "attachment" caseId blobId) payload

    member _.DecryptBlob(caseId, blobId, payload) =
        decrypt (blob "attachment" caseId blobId) payload

    member _.AttachmentDigest(payload: byte array) =
        HMACSHA256.HashData(attachmentKey, payload)

    member _.EncryptCompartment(sequence, ordinal, ownerKind, ownerId, payload) =
        CipherEnvelope.seal
            key
            (compartment "compartment" sequence ordinal ownerKind ownerId)
            payload

    member _.DecryptCompartment(sequence, ordinal, ownerKind, ownerId, payload) =
        decrypt (compartment "compartment" sequence ordinal ownerKind ownerId) payload

    member _.EncryptCompartmentSalt(sequence, ordinal, ownerKind, ownerId, salt) =
        CipherEnvelope.seal
            key
            (compartment "compartment-salt" sequence ordinal ownerKind ownerId)
            salt

    member _.DecryptCompartmentSalt(sequence, ordinal, ownerKind, ownerId, payload) =
        decrypt (compartment "compartment-salt" sequence ordinal ownerKind ownerId) payload

    member _.SaltFingerprint(salt: byte array) = HMACSHA256.HashData(saltKey, salt)

    interface IDisposable with
        member _.Dispose() =
            CryptographicOperations.ZeroMemory key
            CryptographicOperations.ZeroMemory attachmentKey
            CryptographicOperations.ZeroMemory saltKey

module internal InstallationCipher =
    let envelopeOverhead = CipherEnvelope.overhead

    let create installationId =
        new InstallationCipher(installationId, RandomNumberGenerator.GetBytes MasterKey.Size)

    let unwrap (master: MasterKey) installationId (wrapped: byte array) =
        if wrapped.Length <> MasterKey.Size + CipherEnvelope.overhead then
            raise (OperatorFaultException OperatorFault.DataKeyInvalid)

        let key =
            try
                CipherEnvelope.openEnvelope
                    master.Bytes
                    (CipherEnvelope.associatedData "data-key" installationId [])
                    wrapped
            with :? CryptographicException ->
                raise (OperatorFaultException OperatorFault.DataKeyInvalid)

        new InstallationCipher(installationId, key)
