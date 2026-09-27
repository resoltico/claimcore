module ClaimCore.IntegrationTests.RecoveryArtifactKeyCustodyTests

open System
open System.Text
open Expecto
open ClaimCore.Hosting

let private active = Guid.Parse("30000000-0000-4000-8000-000000000001")
let private encryption = Array.init 32 (fun index -> byte (index + 1))
let private mac = Array.init 32 (fun index -> byte (index + 64))

let private roster id issueUntil verifyUntil maximumExports =
    let encryptionText = Convert.ToBase64String(encryption)
    let macText = Convert.ToBase64String(mac)

    $"""{{"version":1,"activeKeyId":"{id:D}","artifactLifetimeSeconds":3600,"keys":[{{"id":"{id:D}","encryptionBase64":"{encryptionText}","macBase64":"{macText}","issueFrom":"2026-09-01T00:00:00+00:00","issueUntil":"{issueUntil}","verifyUntil":"{verifyUntil}","maximumExports":{maximumExports}}}]}}"""
    |> Encoding.UTF8.GetBytes

let private valid () =
    let bytes =
        roster active "2026-10-01T00:00:00+00:00" "2026-10-01T01:00:00+00:00" 4096

    use ring = RecoveryArtifactKeyCustody.parse bytes
    let now = DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero)

    let selected =
        ring.Active now
        |> Option.defaultWith (fun () -> failtest "Active synthetic key required.")

    Expect.equal selected.Id active "Active key ID is explicit"
    Expect.equal selected.MaximumExports 4096 "Per-key export budget is explicit"
    Expect.isSome (ring.Resolve(active, now)) "Current key can verify"

    let old = DateTimeOffset(2026, 10, 1, 0, 30, 0, TimeSpan.Zero)
    Expect.isNone (ring.Active old) "Retired key cannot issue"
    Expect.isSome (ring.Resolve(active, old)) "Retired key verifies until expiry"

let private invalidPolicies () =
    let duplicate =
        roster active "2026-10-01T00:00:00+00:00" "2026-10-01T01:00:00+00:00" 4096
        |> Encoding.UTF8.GetString
        |> fun text -> text.Replace("\"version\":1,", "\"version\":1,\"version\":1,")
        |> Encoding.UTF8.GetBytes

    for bytes in
        [
            roster active "2026-10-01T00:00:00+00:00" "2026-10-01T00:30:00+00:00" 4096
            roster active "2026-10-01T00:00:00+00:00" "2026-10-01T01:00:00+00:00" 65537
            roster Guid.Empty "2026-10-01T00:00:00+00:00" "2026-10-01T01:00:00+00:00" 4096
            duplicate
        ] do
        Expect.throws
            (fun () -> RecoveryArtifactKeyCustody.parse bytes |> ignore)
            "Invalid key identity, budget and verification horizon fail closed"

let tests =
    testList
        "owner-private recovery artifact key policy"
        [
            testCase
                "[CC-REC-001] artifact keys rotate with bounded issue and verify horizons"
                valid
            testCase "[CC-REC-001] artifact key policy rejects unsafe bounds" invalidPolicies
        ]
