module ClaimCore.WitnessTests.KeyRingAdmissionTests

open System
open System.Security.Cryptography
open System.Text
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
