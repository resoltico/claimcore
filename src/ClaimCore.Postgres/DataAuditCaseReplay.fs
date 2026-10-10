namespace ClaimCore.Postgres

open System
open System.Buffers.Binary
open System.Security.Cryptography
open System.Text
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Domain
open ClaimCore.Witness

module internal DataAuditCaseReplay =
    let private appendBounded (hash: IncrementalHash) (bytes: byte array) =
        let length = Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length)
        hash.AppendData(length)
        hash.AppendData(bytes)

    let private appendVerifiedCase
        (hash: IncrementalHash)
        (caseId: Guid)
        (claim: Claim)
        lifecycleHash
        =
        appendBounded hash (caseId.ToByteArray())
        let revision = Array.zeroCreate<byte> 8
        BinaryPrimitives.WriteInt64BigEndian(revision, (Claim.view claim).Version)
        appendBounded hash revision
        appendBounded hash lifecycleHash

    let private readCurrentPage
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        after
        (ct: CancellationToken)
        =
        task {
            use page = new NpgsqlCommand(Sql.listCases, connection, transaction)
            Sql.optional page "after" NpgsqlDbType.Text after
            use! reader = page.ExecuteReaderAsync(ct)
            let current = ResizeArray<Guid * Claim>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(ct)
                reading <- found

                if found then
                    current.Add(reader.GetGuid(reader.GetOrdinal("case_id")), Rows.claim reader)

            return List.ofSeq current
        }

    let private verifyCurrentCase connection transaction witness cutoff ct digest caseId claim =
        task {
            let! lifecycle =
                CaseLifecycleAudit.verifyCase connection transaction witness cutoff caseId claim ct

            let! accepted =
                DataAuditReplay.replayCase
                    connection
                    transaction
                    witness
                    cutoff
                    caseId
                    claim
                    lifecycle.DispositionCount
                    lifecycle.LastBusinessSnapshot
                    ct

            appendVerifiedCase digest caseId claim lifecycle.TipHash
            return accepted, lifecycle.Count
        }

    let replayCases
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        cutoff
        (ct: CancellationToken)
        =
        task {
            let mutable after: string option = None
            let mutable more = true
            let mutable cases = 0L
            let mutable operations = 0L
            let mutable lifecycleEvents = 0L
            use digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            digest.AppendData(Encoding.ASCII.GetBytes("ClaimCore verified case tips v1\n"))

            while more do
                let! current = readCurrentPage connection transaction after ct

                for caseId, claim in current do
                    let! accepted, lifecycleCount =
                        verifyCurrentCase
                            connection
                            transaction
                            witness
                            cutoff
                            ct
                            digest
                            caseId
                            claim

                    cases <- cases + 1L
                    operations <- operations + accepted
                    lifecycleEvents <- lifecycleEvents + lifecycleCount

                match current |> Seq.tryLast with
                | None -> more <- false
                | Some(_, last) ->
                    after <- Some((Claim.view last).Fields.CaseReference)

                    more <-
                        current.Length >
                            ClaimCore.Application.SemanticContract.current.MaximumPageSize

            return cases, operations, lifecycleEvents, digest.GetHashAndReset()
        }
