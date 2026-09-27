module ClaimCore.IntegrationTests.RestorePublicationTests

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open Expecto
open NSec.Cryptography
open ClaimCore.Database

let private stamp (value: DateTimeOffset) =
    value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'")

let private manifest (now: DateTimeOffset) (binary: string) =
    let fields = SortedDictionary<string, objnull>(StringComparer.Ordinal)
    let add key value = fields.Add(key, box value)
    add "format" "claimcore-publication-manifest-1"
    add "publicationId" (Guid.NewGuid().ToString("D"))
    add "verifierBinarySha256" binary
    add "reportSignerKeyId" (Guid.NewGuid().ToString("D"))
    add "checkpointSignerKeyId" (Guid.NewGuid().ToString("D"))
    add "installationId" (Guid.NewGuid().ToString("D"))
    add "lineageId" (Guid.NewGuid().ToString("D"))
    add "epoch" 1L
    add "writerGeneration" 2L
    add "witnessCutoff" 18L
    add "witnessCutoffHash" (String('a', 64))
    add "issuedAt" (stamp (now.AddMinutes(-1.)))
    add "validUntil" (stamp (now.AddMinutes(10.)))
    Encoding.ASCII.GetBytes(JsonSerializer.Serialize(fields) + "\n")

let private anchoredPublication =
    testCase
        "[CC-BACKUP-001] independent publication root pins binary generation and witness tip"
        (fun _ ->
            let algorithm = SignatureAlgorithm.Ed25519
            use key = Key.Create(algorithm)
            use wrong = Key.Create(algorithm)
            let root = key.PublicKey.Export(KeyBlobFormat.RawPublicKey)
            let wrongRoot = wrong.PublicKey.Export(KeyBlobFormat.RawPublicKey)
            let binary = String('b', 64)
            let now = DateTimeOffset.UtcNow
            let bytes = manifest now binary
            let signature = algorithm.Sign(key, bytes)

            let accepted =
                DatabaseRestorePublication.verifyWithRoot root bytes signature binary now

            Expect.isSome accepted "Separately signed exact publication is parsed."
            let pinned = accepted.Value
            Expect.equal pinned.WriterGeneration 2L "Writer generation is signed."
            Expect.equal pinned.WitnessCutoff 18L "Witness cutoff is signed."

            Expect.isNone
                (DatabaseRestorePublication.current ())
                "Test root never installs a production publication root."

            Expect.isNone
                (DatabaseRestorePublication.verifyWithRoot
                    root
                    bytes
                    signature
                    (String('c', 64))
                    now)
                "A newer Database binary cannot reinterpret an old signed publication."

            let changed = Array.copy bytes
            changed[changed.Length - 2] <- byte 'x'

            for candidateRoot, candidateBytes, candidateSignature, candidateBinary, candidateTime in
                [
                    wrongRoot, bytes, signature, binary, now
                    root, changed, signature, binary, now
                    root, bytes, signature, String('c', 64), now
                    root, bytes, signature, binary, now.AddDays(8.)
                ] do
                Expect.isNone
                    (DatabaseRestorePublication.verifyWithRoot
                        candidateRoot
                        candidateBytes
                        candidateSignature
                        candidateBinary
                        candidateTime)
                    "Wrong root, altered bytes, changed binary or expired publication is refused.")

let tests = testList "restore publication" [ anchoredPublication ]
