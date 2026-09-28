module internal ClaimCore.WitnessTests.WitnessAuditContinuationTests

open System
open Expecto
open ClaimCore.Witness
open ClaimCore.WitnessTests.WitnessTestSupport


let private witnessCase17 =
    testCase "[CC-WIT-001] subject scan retains orphan intents after payload pruning" (fun _ ->
        fixture (fun owner writer identity capability ->
            use store = new Store(writer, identity, capability)
            let subject = Guid.NewGuid()
            let unrelated = Guid.NewGuid()

            let first = store.Append(Guid.NewGuid(), Some subject, Intent, keyId, payload 1uy)

            for i in 2..35 do
                store.Append(Guid.NewGuid(), Some subject, Intent, keyId, payload (byte i))
                |> ignore

            store.Append(Guid.NewGuid(), None, Intent, keyId, payload 36uy) |> ignore

            store.Append(Guid.NewGuid(), Some unrelated, Intent, keyId, payload 37uy)
            |> ignore

            let tip = store.Snapshot()

            run
                owner
                $"DELETE FROM claimcore_witness.journal_payloads WHERE sequence={first.Sequence}"

            let pages = ResizeArray<SubjectOperation list>()

            let result = store.ReadSubjectOperations(subject, tip.TipSequence, pages.Add)

            let metadata =
                store.ReadMetadataPage(0L, Array.zeroCreate<byte> 32, tip.TipSequence, 32)

            Expect.isFalse
                metadata.Items.Head.PayloadPresent
                "Metadata-only global scan exposes absent ciphertext without dropping the row"

            Expect.equal
                (store.TryReadVerifiedEntryHash(first.Sequence))
                (Some first.EntryHash)
                "Pruned ciphertext does not prevent exact historical metadata proof"

            Expect.equal result.CutoffSequence tip.TipSequence "Exact cutoff was scanned"
            Expect.equal result.CutoffHash tip.TipHash "Global hash reaches observed tip"

            Expect.equal
                (store.TryReadVerifiedEntryHash(tip.TipSequence + 1L))
                None
                "A future checkpoint has no historical hash"

            Expect.equal result.IntentCount 35L "Every case intent, including orphan, is retained"

            Expect.equal
                (pages |> Seq.sumBy List.length)
                35
                "Only target case intents were emitted"

            Expect.isTrue
                (pages |> Seq.forall (fun page -> page.Length <= 32))
                "Pages stay bounded"

            let firstObserved = pages[0] |> List.head

            Expect.equal
                firstObserved.OperationId
                first.OperationId
                "Pruned payload does not hide intent"

            Expect.equal first.ScopeKind Case "Intent is explicitly case-scoped"
            Expect.equal first.SubjectCaseId (Some subject) "Ticket binds exact case"))

let private witnessCase18 =
    testCase "[CC-WIT-001] subject scan rejects intervening tamper and scope weakening" (fun _ ->
        fixture (fun owner writer identity capability ->
            use store = new Store(writer, identity, capability)
            let subject = Guid.NewGuid()

            for i in 1..34 do
                store.Append(Guid.NewGuid(), Some subject, Intent, keyId, payload (byte i))
                |> ignore

            let tip = store.Snapshot()

            run
                owner
                "UPDATE claimcore_witness.journal SET entry_hash=decode(repeat('ff',32),'hex') WHERE sequence=33"

            let mutable tentative = 0

            Expect.throws
                (fun () ->
                    store.ReadSubjectOperations(
                        subject,
                        tip.TipSequence,
                        fun page -> tentative <- tentative + page.Length
                    )
                    |> ignore)
                "An intervening bad global row is rejected"

            Expect.throws
                (fun () -> store.TryReadVerifiedEntryHash(tip.TipSequence) |> ignore)
                "Historical hash lookup cannot skip a forged middle row"

            let firstPage =
                store.ReadMetadataPage(0L, Array.zeroCreate<byte> 32, tip.TipSequence, 32)

            let last = firstPage.Items |> List.last

            Expect.throws
                (fun () ->
                    store.ReadMetadataPage(32L, last.Ticket.EntryHash, tip.TipSequence, 32)
                    |> ignore)
                "Metadata page rejects a changed middle CASE link"

            Expect.equal tentative 32 "Earlier emitted pages were tentative, not clearance"))

let private witnessCase19 =
    testCase "[CC-WIT-001] scope and unique-index changes close admission" (fun _ ->
        fixture (fun owner writer identity capability ->
            use store = new Store(writer, identity, capability)

            Expect.throws
                (fun () ->
                    store.Append(Guid.NewGuid(), Some Guid.Empty, Intent, keyId, payload 1uy)
                    |> ignore)
                "Empty case identity is not an installation event"

            run
                owner
                "ALTER TABLE claimcore_witness.journal DROP CONSTRAINT journal_installation_id_operation_id_phase_key"

            Expect.throws (fun () -> store.Admit()) "Removed uniqueness closes admission"))


let continuationCases = [ witnessCase17; witnessCase18; witnessCase19 ]
