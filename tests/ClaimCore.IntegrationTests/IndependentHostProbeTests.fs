module ClaimCore.IntegrationTests.IndependentHostProbeTests

open System
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Database
open ClaimCore.IntegrationTests.IndependentHostProbeFixture

let private now () =
    DateTimeOffset.UtcNow
        .ToUniversalTime()
        .AddTicks(-(DateTimeOffset.UtcNow.Ticks % TimeSpan.TicksPerSecond))

let private verify fixture (entry: JsonElement) current =
    DatabaseIndependentHostChildren.verify
        entry
        fixture.Pin
        fixture.PublicPem
        fixture.Nonce
        fixture.SupplementSha
        fixture.Backup
        fixture.Tail
        fixture.Now
        current

let private forgedDigest fixture =
    let oldDigest =
        fixture.Entry.RootElement.GetProperty("probeSha256").GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "The synthetic digest is missing.")

    let raw = fixture.Entry.RootElement.GetRawText()

    let changed =
        raw.Replace(
            "\"probeSha256\":\"" + oldDigest + "\"",
            "\"probeSha256\":\"" + String.replicate 64 "0" + "\"",
            StringComparison.Ordinal
        )

    JsonDocument.Parse(Encoding.ASCII.GetBytes(changed + "\n"))

let tests =
    testList
        "independent archive host proof"
        [
            testCase
                "[CC-BACKUP-001] signed archive observation binds every final WAL object"
                (fun _ ->
                    use fixture = createArchiveProbe (now ())

                    let digest, expiry =
                        verify fixture fixture.Entry.RootElement (Some fixture.Now)

                    Expect.equal
                        digest
                        (fixture.Entry.RootElement.GetProperty("probeSha256").GetString()
                         |> Option.ofObj
                         |> Option.defaultValue "")
                        "Exact signed child digest survives native verification."

                    Expect.isGreaterThan
                        expiry
                        fixture.Now
                        "The child is fresh at the database clock.")
            testCase "[CC-BACKUP-001] forged or stale archive observation is refused" (fun _ ->
                use fixture = createArchiveProbe (now ())

                use forged = forgedDigest fixture

                Expect.throws
                    (fun () -> verify fixture forged.RootElement (Some fixture.Now) |> ignore)
                    "An aggregate summary cannot forge the signed child."

                Expect.throws
                    (fun () ->
                        verify
                            fixture
                            fixture.Entry.RootElement
                            (Some(fixture.Now.AddSeconds(61.)))
                        |> ignore)
                    "An expired child cannot qualify a current deployment."

                verify fixture fixture.Entry.RootElement None |> ignore)
            testCase
                "[CC-BACKUP-001] signed archive observation missing one final WAL object is refused"
                (fun _ ->
                    use fixture = createArchiveProbeWithFinalObjects (now ()) false

                    Expect.throws
                        (fun () ->
                            verify fixture fixture.Entry.RootElement (Some fixture.Now) |> ignore)
                        "A valid archive signature over an incomplete final set is insufficient.")
        ]
