namespace ClaimCore.Witness

open System
open System.Data.Common
open System.Security.Cryptography
open System.Text

module internal WitnessJournal =
    let private optionalGuid (reader: DbDataReader) index =
        if reader.IsDBNull(index) then
            None
        else
            Some(reader.GetGuid(index))

    let private metadata
        (identity: Identity)
        (lineage: Guid)
        (epoch: int64)
        (sequence: int64)
        (operation: Guid)
        (phase: Phase)
        (keyId: Guid)
        (scopeKind: ScopeKind)
        (subjectCaseId: Guid option)
        =
        identity.InstallationId.ToString("D")
        + ":"
        + lineage.ToString("D")
        + ":"
        + epoch.ToString(Globalization.CultureInfo.InvariantCulture)
        + ":"
        + sequence.ToString(Globalization.CultureInfo.InvariantCulture)
        + ":"
        + operation.ToString("D")
        + ":"
        + Encoding.phase phase
        + ":"
        + keyId.ToString("D")
        + ":"
        + Encoding.scope scopeKind
        + ":"
        + (subjectCaseId
           |> Option.map (fun value -> value.ToString("D"))
           |> Option.defaultValue "-")

    let private requireChain
        (identity: Identity)
        expectedSequence
        expectedPreviousHash
        sequence
        epoch
        lineage
        (scopeKind: ScopeKind)
        (subjectCaseId: Guid option)
        (payloadCaseId: Guid option)
        storedPrevious
        (digest: byte array)
        (payload: byte array)
        (entryHash: byte array)
        computed
        =
        if
            sequence <> expectedSequence
            || epoch <> identity.Epoch
            || lineage <> identity.LineageId
            || (scopeKind = Case) <> subjectCaseId.IsSome
            || subjectCaseId = Some Guid.Empty
            || subjectCaseId <> payloadCaseId
            || storedPrevious <> expectedPreviousHash
            || digest.Length <> 32
            || entryHash.Length <> 32
            || digest <> SHA256.HashData(payload)
            || entryHash <> computed
        then
            invalidOp "Witness journal chain diverged."

    let private ticket
        sequence
        epoch
        keyId
        entryHash
        digest
        operation
        phase
        scopeKind
        subjectCaseId
        : Ticket =
        {
            Sequence = sequence
            Epoch = epoch
            KeyId = keyId
            EntryHash = entryHash
            PayloadHash = digest
            OperationId = operation
            Phase = phase
            ScopeKind = scopeKind
            SubjectCaseId = subjectCaseId
        }

    let readRow (identity: Identity) expectedSequence expectedPreviousHash (reader: DbDataReader) =
        let sequence = reader.GetInt64(0)
        let epoch = reader.GetInt64(1)
        let operation = reader.GetGuid(2)
        let phase = Encoding.parsePhase (reader.GetString(3))
        let keyId = reader.GetGuid(4)
        let payload = reader.GetFieldValue<byte array>(5)
        let digest = reader.GetFieldValue<byte array>(6)
        let storedPrevious = reader.GetFieldValue<byte array>(7)
        let entryHash = reader.GetFieldValue<byte array>(8)
        let lineage = reader.GetGuid(9)

        let subjectCaseId = optionalGuid reader 10
        let payloadCaseId = optionalGuid reader 11
        let scopeKind = Encoding.parseScope (reader.GetString(12))

        let metadata =
            metadata identity lineage epoch sequence operation phase keyId scopeKind subjectCaseId

        let computed =
            SHA256.HashData(
                Array.concat
                    [ storedPrevious; System.Text.Encoding.UTF8.GetBytes(metadata); digest ]
            )

        requireChain
            identity
            expectedSequence
            expectedPreviousHash
            sequence
            epoch
            lineage
            scopeKind
            subjectCaseId
            payloadCaseId
            storedPrevious
            digest
            payload
            entryHash
            computed

        let ticket =
            ticket sequence epoch keyId entryHash digest operation phase scopeKind subjectCaseId

        {
            Evidence =
                {
                    Ticket = ticket
                    EncryptedPayload = payload
                }
            PreviousHash = storedPrevious
        }

    let readMetadataRow
        (identity: Identity)
        expectedSequence
        expectedPreviousHash
        (reader: DbDataReader)
        =
        let sequence = reader.GetInt64(0)
        let epoch = reader.GetInt64(1)
        let operation = reader.GetGuid(2)
        let phase = Encoding.parsePhase (reader.GetString(3))
        let keyId = reader.GetGuid(4)
        let digest = reader.GetFieldValue<byte array>(5)
        let storedPrevious = reader.GetFieldValue<byte array>(6)
        let entryHash = reader.GetFieldValue<byte array>(7)
        let lineage = reader.GetGuid(8)
        let scopeKind = Encoding.parseScope (reader.GetString(9))
        let subjectCaseId = optionalGuid reader 10

        let computed =
            SHA256.HashData(
                Array.concat
                    [
                        storedPrevious
                        System.Text.Encoding.UTF8.GetBytes(
                            metadata
                                identity
                                lineage
                                epoch
                                sequence
                                operation
                                phase
                                keyId
                                scopeKind
                                subjectCaseId
                        )
                        digest
                    ]
            )

        if
            sequence <> expectedSequence
            || epoch <> identity.Epoch
            || lineage <> identity.LineageId
            || storedPrevious <> expectedPreviousHash
            || digest.Length <> 32
            || entryHash.Length <> 32
            || entryHash <> computed
            || (scopeKind = Case) <> subjectCaseId.IsSome
            || subjectCaseId = Some Guid.Empty
        then
            invalidOp "Witness metadata chain or scope diverged."

        ticket sequence epoch keyId entryHash digest operation phase scopeKind subjectCaseId
