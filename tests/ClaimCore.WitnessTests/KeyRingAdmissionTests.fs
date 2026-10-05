module ClaimCore.WitnessTests.KeyRingAdmissionTests

open System
open System.Security.Cryptography
open System.Text
open Npgsql
open ClaimCore.WitnessTests.WitnessTestSupport
open Expecto
open ClaimCore.Witness

[<Tests>]
let tests =
    testCase
        "[CC-WIT-001] witness rotation rejects reused material and ambiguous key files"
        (fun _ ->
            let oldId = Guid.NewGuid()
            let newId = Guid.NewGuid()
            let material = RandomNumberGenerator.GetBytes(32)

            Expect.throws
                (fun () -> new KeyRing(newId, [ oldId, material; newId, material ]) |> ignore)
                "A changed ID cannot reuse an AES-GCM key"

            Expect.throws
                (fun () -> new Cipher(Array.zeroCreate 32) |> ignore)
                "A zero key is not credible private material"

            let encoded = Convert.ToBase64String(material)

            let valid =
                $"{{\"version\":1,\"activeKeyId\":\"{oldId:D}\",\"keys\":[{{\"id\":\"{oldId:D}\",\"materialBase64\":\"{encoded}\"}}]}}"

            use parsed = KeyRingCodec.parse (Encoding.UTF8.GetBytes(valid))
            Expect.equal parsed.ActiveKeyId oldId "A unique well-shaped ring is admitted"

            let duplicate = valid.Replace("\"version\":1,", "\"version\":1,\"version\":1,")

            Expect.throws
                (fun () -> KeyRingCodec.parse (Encoding.UTF8.GetBytes(duplicate)) |> ignore)
                "Duplicate root properties cannot select ambiguous authority")

let rotation =
    testCase "[CC-WIT-001] owner rotation advances journal and fences old key" (fun _ ->
        fixture (fun owner writer identity capability ->
            use store = new Store(writer, identity, capability)

            let first =
                (store.Append(Guid.NewGuid(), None, Intent, keyId, payload 1uy, cancellation)
                 |> await)

            let newId = Guid.NewGuid()
            let newKey = RandomNumberGenerator.GetBytes(32)
            use custody = new KeyRing(newId, [ newId, newKey ]) :> IKeyCustody
            let keyCheck = KeyCheck.create custody identity.InstallationId identity.LineageId
            let rotation = Guid.NewGuid()

            let encrypted =
                KeyCheck.rotationEnvelope
                    custody
                    identity.InstallationId
                    identity.LineageId
                    identity.Epoch
                    rotation
                    keyId
                    newId

            let ticket =
                KeyRotation.rotateKey
                    owner
                    identity
                    rotation
                    keyId
                    newId
                    keyCheck
                    encrypted
                    capability

            Expect.equal ticket.Sequence (first.Sequence + 1L) "Rotation is journaled"

            let evidence =
                (store.TryReadEvidence(rotation, KeyRotated, cancellation) |> await)
                |> Option.defaultWith (fun () -> failtest "Rotation evidence must exist.")

            KeyCheck.verifyRotation
                custody
                identity.InstallationId
                identity.LineageId
                identity.Epoch
                rotation
                keyId
                newId
                evidence.EncryptedPayload

            Expect.equal
                (fst ((store.ReadKeyCheck(cancellation) |> await)))
                newId
                "New key marker is active"

            Expect.throws
                (fun () ->
                    (store.Append(Guid.NewGuid(), None, Intent, keyId, payload 4uy, cancellation)
                     |> await)
                    |> ignore)
                "Stale writer key is fenced"

            let after =
                (store.Append(Guid.NewGuid(), None, Intent, newId, payload 5uy, cancellation)
                 |> await)

            Expect.equal after.Sequence (ticket.Sequence + 1L) "New key appends after rotation"))
