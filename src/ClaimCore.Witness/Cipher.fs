namespace ClaimCore.Witness

open System
open System.Security.Cryptography
open System.Collections.Generic
open System.Text

/// A versioned AES-256-GCM envelope. The caller supplies a separately custodied key; the
/// database and adapter never receive it. One random nonce is generated per candidate.
[<Sealed>]
type Cipher(keyMaterial: byte array) =
    let key =
        if keyMaterial.Length <> 32 then
            invalidArg (nameof keyMaterial) "Witness key must be 256 bits."

        Array.copy keyMaterial

    let header = [| 0x43uy; 0x43uy; 0x57uy; 0x31uy |]

    member _.Encrypt(associatedData: byte array, plaintext: byte array) =
        let nonce = RandomNumberGenerator.GetBytes(12)
        let tag = Array.zeroCreate<byte> 16
        let ciphertext = Array.zeroCreate<byte> plaintext.Length
        use algorithm = new AesGcm(key, 16)
        algorithm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData)
        Array.concat [ header; nonce; tag; ciphertext ]

    member _.Decrypt(associatedData: byte array, envelope: byte array) =
        if envelope.Length < 32 || envelope[0..3] <> header then
            invalidOp "Witness encryption envelope is invalid."

        let nonce = envelope[4..15]
        let tag = envelope[16..31]
        let ciphertext = envelope[32..]
        let plaintext = Array.zeroCreate<byte> ciphertext.Length
        use algorithm = new AesGcm(key, 16)
        algorithm.Decrypt(nonce, ciphertext, tag, plaintext, associatedData)
        plaintext

    interface IDisposable with
        member _.Dispose() = CryptographicOperations.ZeroMemory(key)

/// Owner-private custody supplies the active key and every retained historical key by opaque ID.
/// The witness database receives only IDs and authenticated ciphertext, never key material.
type IKeyCustody =
    inherit IDisposable
    abstract ActiveKeyId: Guid
    abstract HasKey: Guid -> bool
    abstract Encrypt: Guid * byte array * byte array -> byte array
    abstract Decrypt: Guid * byte array * byte array -> byte array

[<Sealed>]
type KeyRing(activeKeyId: Guid, keys: seq<Guid * byte array>) =
    let ciphers = Dictionary<Guid, Cipher>()

    do
        try
            for keyId, bytes in keys do
                if keyId = Guid.Empty || ciphers.ContainsKey(keyId) then
                    invalidArg (nameof keys) "Witness key IDs must be unique and nonempty."

                ciphers.Add(keyId, new Cipher(bytes))

            if activeKeyId = Guid.Empty || not (ciphers.ContainsKey(activeKeyId)) then
                invalidArg (nameof activeKeyId) "Active witness key is unavailable."

            if ciphers.Count > 64 then
                invalidArg (nameof keys) "Witness key ring exceeds the bounded rotation limit."
        with _ ->
            for cipher in ciphers.Values do
                (cipher :> IDisposable).Dispose()

            reraise ()

    interface IKeyCustody with
        member _.ActiveKeyId = activeKeyId
        member _.HasKey keyId = ciphers.ContainsKey(keyId)

        member _.Encrypt(keyId, associatedData, plaintext) =
            match ciphers.TryGetValue(keyId) with
            | true, cipher -> cipher.Encrypt(associatedData, plaintext)
            | _ -> invalidOp "Historical witness key is unavailable."

        member _.Decrypt(keyId, associatedData, envelope) =
            match ciphers.TryGetValue(keyId) with
            | true, cipher -> cipher.Decrypt(associatedData, envelope)
            | _ -> invalidOp "Historical witness key is unavailable."

        member _.Dispose() =
            for cipher in ciphers.Values do
                (cipher :> IDisposable).Dispose()

            ciphers.Clear()

module KeyCheck =
    let private marker = Encoding.ASCII.GetBytes("CC-WITNESS-KEY-CHECK-1")

    let associatedData (installationId: Guid) (lineageId: Guid) (keyId: Guid) =
        Encoding.ASCII.GetBytes(
            installationId.ToString("D")
            + ":"
            + lineageId.ToString("D")
            + ":"
            + keyId.ToString("D")
            + ":KEY_CHECK"
        )

    let create (custody: IKeyCustody) installationId lineageId =
        custody.Encrypt(
            custody.ActiveKeyId,
            associatedData installationId lineageId custody.ActiveKeyId,
            marker
        )

    let verify (custody: IKeyCustody) installationId lineageId keyId envelope =
        let plain =
            custody.Decrypt(keyId, associatedData installationId lineageId keyId, envelope)

        try
            if plain <> marker then
                invalidOp "Witness key-check envelope mismatches."
        finally
            CryptographicOperations.ZeroMemory(plain)

    let private rotationPlain (operationId: Guid) (oldKeyId: Guid) (newKeyId: Guid) =
        Encoding.ASCII.GetBytes(
            "CC-WITNESS-KEY-ROTATED-1:"
            + operationId.ToString("D")
            + ":"
            + oldKeyId.ToString("D")
            + ":"
            + newKeyId.ToString("D")
        )

    let private rotationAad
        (installationId: Guid)
        (lineageId: Guid)
        (epoch: int64)
        (operationId: Guid)
        =
        Encoding.ASCII.GetBytes(
            installationId.ToString("D")
            + ":"
            + lineageId.ToString("D")
            + ":"
            + epoch.ToString(Globalization.CultureInfo.InvariantCulture)
            + ":"
            + operationId.ToString("D")
            + ":KEY_ROTATED"
        )

    let rotationEnvelope
        (custody: IKeyCustody)
        installationId
        lineageId
        epoch
        operationId
        oldKeyId
        newKeyId
        =
        if custody.ActiveKeyId <> newKeyId then
            invalidOp "New witness key is not active in custody."

        let plain = rotationPlain operationId oldKeyId newKeyId

        try
            custody.Encrypt(newKeyId, rotationAad installationId lineageId epoch operationId, plain)
        finally
            CryptographicOperations.ZeroMemory(plain)

    let verifyRotation
        (custody: IKeyCustody)
        installationId
        lineageId
        epoch
        operationId
        oldKeyId
        newKeyId
        envelope
        =
        let expected = rotationPlain operationId oldKeyId newKeyId

        let plain =
            custody.Decrypt(
                newKeyId,
                rotationAad installationId lineageId epoch operationId,
                envelope
            )

        try
            if plain <> expected then
                invalidOp "Witness key rotation evidence mismatches."
        finally
            CryptographicOperations.ZeroMemory(plain)
            CryptographicOperations.ZeroMemory(expected)
