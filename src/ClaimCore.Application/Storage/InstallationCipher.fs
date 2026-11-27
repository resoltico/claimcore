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

/// Independent HKDF keys bind ciphertext to a purpose and an admitted generation.
[<Sealed>]
type internal InstallationCipher
    (installationId: Guid, key: byte array, active: Guid, admitted: Set<Guid>, allowance: int64) =
    let identityBytes = 16

    let derive label salt =
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            key,
            MasterKey.Size,
            salt,
            Encoding.UTF8.GetBytes("claimcore/" + label)
        )

    // Equality keys deliberately survive an encryption rotation; the comparison data remains erasable.
    let attachmentKey = derive "attachment-digest" Array.empty
    let saltKey = derive "compartment-salt-fingerprint" Array.empty

    let mutable remaining = allowance

    let identity (generation: Guid) = generation.ToByteArray(true)

    let seal purpose salt aad plain =
        let generation = identity active
        let useKey = derive purpose (Array.append generation salt)

        try
            CipherEnvelope.seal useKey (Array.append generation aad) plain
            |> Array.append (Array.append generation salt)
        finally
            CryptographicOperations.ZeroMemory useKey

    let durableSeal purpose aad plain =
        if System.Threading.Interlocked.Decrement(&remaining) < 0L then
            invalidOp "Durable encryption requires an unused reservation."

        seal purpose Array.empty aad plain

    let openSealed purpose saltLength aad (envelope: byte array) activeOnly =
        if envelope.Length < CipherEnvelope.overhead + identityBytes + saltLength then
            raise (CryptographicException "Invalid data envelope size.")

        let generation = Guid(ReadOnlySpan<byte>(envelope, 0, identityBytes), true)

        if not (admitted.Contains generation) || (activeOnly && generation <> active) then
            raise (CryptographicException "The encryption generation is unavailable.")

        let generationBytes = identity generation
        let headerLength = identityBytes + saltLength
        let useKey = derive purpose envelope[0 .. headerLength - 1]

        try
            CipherEnvelope.openEnvelope
                useKey
                (Array.append generationBytes aad)
                envelope[headerLength..]
        finally
            CryptographicOperations.ZeroMemory useKey

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

    let decrypt purpose aad payload =
        try
            openSealed purpose 0 aad payload false
        with :? CryptographicException ->
            raise (RecordCorruptException "Stored data authentication failed.")

    member _.InstallationId = installationId
    member _.Generation = active

    member _.ForEncryption(count) =
        new InstallationCipher(installationId, Array.copy key, active, admitted, count)

    member _.SealCursor(purpose, requester: Guid, plain: byte array) =
        let aad =
            CipherEnvelope.associatedData purpose installationId [ requester.ToString("D") ]

        seal "cursor" (RandomNumberGenerator.GetBytes 32) aad plain
        |> System.Buffers.Text.Base64Url.EncodeToString

    member _.OpenCursor(purpose, requester: Guid, token: string) =
        try
            let envelope = System.Buffers.Text.Base64Url.DecodeFromChars(token.AsSpan())
            let canonical = System.Buffers.Text.Base64Url.EncodeToString envelope

            if canonical <> token then
                None
            else
                let aad =
                    CipherEnvelope.associatedData purpose installationId [ requester.ToString("D") ]

                Some(openSealed "cursor" 32 aad envelope true)
        with
        | :? FormatException
        | :? CryptographicException -> None

    member _.Wrap(master: MasterKey) =
        let salt = RandomNumberGenerator.GetBytes 32
        let aad = CipherEnvelope.associatedData "data-key" installationId []

        // Each wrapping operation uses its own HKDF key and a fresh GCM IV.
        let wrappingKey =
            HKDF.DeriveKey(HashAlgorithmName.SHA256, master.Bytes, MasterKey.Size, salt, aad)

        try
            CipherEnvelope.seal wrappingKey aad key |> Array.append salt
        finally
            CryptographicOperations.ZeroMemory wrappingKey

    member _.EncryptBlob(caseId, blobId, payload) =
        durableSeal "attachment" (blob "attachment" caseId blobId) payload

    member _.DecryptBlob(caseId, blobId, payload) =
        decrypt "attachment" (blob "attachment" caseId blobId) payload

    member _.AttachmentDigest(payload: byte array) =
        HMACSHA256.HashData(attachmentKey, payload)

    member _.EncryptCompartment(sequence, ordinal, ownerKind, ownerId, payload) =
        durableSeal
            "compartment"
            (compartment "compartment" sequence ordinal ownerKind ownerId)
            payload

    member _.DecryptCompartment(sequence, ordinal, ownerKind, ownerId, payload) =
        decrypt "compartment" (compartment "compartment" sequence ordinal ownerKind ownerId) payload

    member _.EncryptCompartmentSalt(sequence, ordinal, ownerKind, ownerId, salt) =
        durableSeal
            "compartment-salt"
            (compartment "compartment-salt" sequence ordinal ownerKind ownerId)
            salt

    member _.DecryptCompartmentSalt(sequence, ordinal, ownerKind, ownerId, payload) =
        decrypt
            "compartment-salt"
            (compartment "compartment-salt" sequence ordinal ownerKind ownerId)
            payload

    member _.SaltFingerprint(salt: byte array) = HMACSHA256.HashData(saltKey, salt)

    interface IDisposable with
        member _.Dispose() =
            CryptographicOperations.ZeroMemory key
            CryptographicOperations.ZeroMemory attachmentKey
            CryptographicOperations.ZeroMemory saltKey

module internal InstallationCipher =
    // Two values per retained compartment and at most one attachment per operation.
    let operationAllowance =
        int64 (2 * ClaimCore.RecordFormat.OperationStructure.MaximumCompartments + 1)

    let envelopeOverhead = 16 + CipherEnvelope.overhead
    let wrappedKeyBytes = 32 + MasterKey.Size + CipherEnvelope.overhead

    let create installationId =
        let active = Guid.NewGuid()

        new InstallationCipher(
            installationId,
            RandomNumberGenerator.GetBytes MasterKey.Size,
            active,
            Set.singleton active,
            operationAllowance
        )

    let unwrap
        (master: MasterKey)
        installationId
        active
        (admitted: Set<Guid>)
        (wrapped: byte array)
        =
        if wrapped.Length <> wrappedKeyBytes || not (admitted.Contains active) then
            raise (OperatorFaultException OperatorFault.DataKeyInvalid)

        let aad = CipherEnvelope.associatedData "data-key" installationId []

        let wrappingKey =
            HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                master.Bytes,
                MasterKey.Size,
                wrapped[0..31],
                aad
            )

        let key =
            try
                try
                    CipherEnvelope.openEnvelope wrappingKey aad wrapped[32..]
                with :? CryptographicException ->
                    raise (OperatorFaultException OperatorFault.DataKeyInvalid)
            finally
                CryptographicOperations.ZeroMemory wrappingKey

        new InstallationCipher(installationId, key, active, admitted, 0L)
