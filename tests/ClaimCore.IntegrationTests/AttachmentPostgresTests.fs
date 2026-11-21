module ClaimCore.IntegrationTests.AttachmentPostgresTests

open System
open System.Text
open System.Security.Cryptography
open System.Globalization
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Application.Verification
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let internal target = CaseSelector.Reference "synthetic-a"
let internal bytes = Encoding.UTF8.GetBytes "abc"

let private digest =
    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"

let private emptyDigest =
    "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"

let private firstName = "private-canary.txt"

let internal attachmentGuid fixture number =
    id fixture number |> AttachmentId.create |> Option.get

let private draftFor target fixture number name count digest =
    Submissions.createRaw
        "attachment.add"
        (id fixture number)
        (OperationBinding.Attachment(target, None))
        ([
            InputTarget.AttachmentName, name
            InputTarget.AttachmentLength, count
            InputTarget.AttachmentDigest, digest
         ]
         |> List.map (fun (target, value) -> Submissions.text (Submissions.input target) value)
         |> Map.ofList)

let private draft = draftFor target

let internal add fixture number name = draft fixture number name "3" digest

let private empty fixture number =
    draft fixture number "empty" "0" emptyDigest

let private fromPayload fixture number (payload: byte array) =
    draft
        fixture
        number
        "quota.bin"
        (payload.Length.ToString(CultureInfo.InvariantCulture))
        (SHA256.HashData(payload) |> Convert.ToHexStringLower)

let internal withdraw fixture number attachment =
    Submissions.createRaw
        "attachment.withdraw"
        (id fixture number)
        (OperationBinding.Attachment(target, Some(AttachmentId.value attachment)))
        Map.empty

let internal erase fixture number =
    request fixture number "synthetic-a" 1L "case.erase" Map.empty

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected attachment read: %A" other

let internal list owner =
    Service.caseAttachments owner target token

let internal download owner attachment =
    Service.downloadAttachment
        owner
        {
            Target = target
            AttachmentId = attachment
        }
        token

let internal upload owner command payload =
    Service.executeAttachmentUpload owner command (ReadOnlyMemory<byte> payload) token

let private preview owner attachment =
    Service.previewAttachment
        owner
        {
            Target = target
            AttachmentId = attachment
        }
        token

let private attachmentReceipt (receipt: OperationReceipt) =
    match receipt.Subject with
    | ReceiptSubject.Attachment attachment -> attachment
    | subject -> failtestf "Expected attachment receipt: %A" subject

let internal status owner operation =
    task {
        let! lookup = Service.operationGet owner { OperationId = operation } token

        match read lookup.Outcome with
        | OperationLookupResult.Accepted(receipt, _) -> return (attachmentReceipt receipt).Snapshot
        | other -> return failtestf "Expected confirmed attachment operation: %A" other
    }

let private history owner after limit =
    Service.timeline
        owner
        {
            Target = target
            Filter = Some TimelineFilter.Attachments
            AfterCursor = after
            Limit = limit
        }
        token

let private assertUnavailable result =
    match result with
    | Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable) -> ()
    | other -> failtestf "Expected indistinguishable attachment refusal, got %A" other

let private assertDownload expected (result: Outcome<AttachmentContent>) =
    let content = read result

    Expect.sequenceEqual
        ((VerifiedAttachment.bytes content.Bytes).ToArray())
        expected
        "Exact bytes cross the PostgreSQL store boundary"

let private blockedType =
    Refusal.invalidInput (InputTarget.AttachmentName, Violation.AttachmentTypeRefused)

let private expectBlocked (reply: ExecuteReply) =
    Expect.equal
        reply.Outcome
        (ExecuteResult.Rejected blockedType)
        "The type decision refuses before retention checks"

let private grantAttachmentRoles (fixture: Fixture) owner =
    task {
        for number, role in [ 11, Role.DataSteward; 12, Role.Auditor ] do
            let! reply =
                grant
                    owner
                    (id fixture number)
                    fixture.Owner
                    {
                        Role = role
                        Scope = GrantTarget.Installation
                    }
                    true

            acceptedPeople reply
    }

let internal setup (database: PostgresFixture.Database) =
    task {
        let! owner = populated database
        do! grantAttachmentRoles database.Fixture owner
        return owner
    }

let private maintainedAt (database: PostgresFixture.Database) generation =
    task {
        let pauseId = Guid.Parse "10000000-0000-4000-8000-000000000099"

        let! mode =
            RegisterMaintenanceStore.change
                database.OwnerSource
                database.MasterKey
                (MaintenanceChange.Acquire(generation, pauseId))
                token

        Expect.isTrue mode.State.Active "The owner explicitly pauses register writes"
        return MaintenanceHandle.create (generation + 1L) pauseId |> Option.get
    }

let private maintained database = maintainedAt database 0L

let private lifecycle (database: PostgresFixture.Database) =
    task {
        let! owner = setup database
        let fixture = database.Fixture
        let staleErase = erase fixture 20
        let first = add fixture 21 firstName
        let second = add fixture 22 "duplicate-name.txt"

        let! bypass = Service.executeSubmission owner first token

        match bypass.Outcome with
        | ExecuteResult.Rejected(Refusal.InvalidInput _) -> ()
        | other -> failtestf "Metadata-only execution accepted an upload: %A" other

        let forbidden = add fixture 29 "file.exe"
        let! beforeRefusal = verified database
        let! review = Service.previewSubmission owner forbidden token

        Expect.equal
            review.Outcome
            (Outcome.Rejected blockedType)
            "Authorized metadata review identifies the blocked name"

        let! blocked = upload owner forbidden bytes
        expectBlocked blocked
        let! afterRefusal = verified database
        Expect.equal afterRefusal beforeRefusal "Refusal leaves the verified ledger unchanged"

        let! blobs =
            DatabaseSession.read database.OwnerSource token (fun connection ->
                DatabaseSession.rows
                    connection
                    "SELECT count(*) FROM claimcore.attachment_blobs WHERE blob_id=$1"
                    [| box forbidden.OperationId |]
                    token)

        Expect.equal (DatabaseSession.value<int64> 0 blobs.Head) 0L "Refusal stores no blob"

        for contender in [ first; second ] do
            let! review = Service.previewSubmission owner contender token

            match review.Outcome with
            | Outcome.Succeeded(Previewed.Reviewed _) -> ()
            | other ->
                failtestf "Both competing intents must receive review before either write: %A" other

        let! raced = Task.WhenAll [| upload owner first bytes; upload owner second bytes |]

        let acceptedReply =
            raced
            |> Array.find (fun reply ->
                match reply.Outcome with
                | ExecuteResult.Accepted _ -> true
                | _ -> false)

        let attachment = accepted acceptedReply |> attachmentReceipt
        let current = attachment.AttachmentId

        let submitted =
            if acceptedReply.OperationId = first.OperationId then
                first
            else
                second

        Expect.equal
            (raced
             |> Array.filter (fun reply ->
                 match reply.Outcome with
                 | ExecuteResult.Rejected(Refusal.AttachmentDuplicate duplicate) ->
                     duplicate = current
                 | _ -> false)
             |> Array.length)
            1
            "Same-case concurrent uploads produce one acceptance and one typed duplicate"

        let! checkedBytes = download owner current
        assertDownload bytes checkedBytes
        let! duplicateType = upload owner (add fixture 30 "matching.js") bytes
        expectBlocked duplicateType
        let! exactReplay = upload owner submitted bytes

        expectReceipt
            (replayReceipt exactReplay)
            (accepted acceptedReply)
            "Exact retry keeps the receipt"

        let secondTarget = CaseSelector.Reference "synthetic-b"

        let! opened =
            Service.executeSubmission
                owner
                (request
                    fixture
                    40
                    "synthetic-b"
                    0L
                    "case.open"
                    (Submissions.fields (RegistrationInput.values registration)))
                token

        accepted opened |> ignore

        let wrongParent =
            {
                Target = secondTarget
                AttachmentId = current
            }

        let! wrong = Service.downloadAttachment owner wrongParent token
        assertUnavailable wrong
        let! wrongPreview = Service.previewAttachment owner wrongParent token
        assertUnavailable wrongPreview

        let service =
            PrincipalKey.service "https://issuer.example/" "attachment-reader"
            |> Result.defaultWith (string >> invalidOp)

        let! registration =
            personSubmission
                fixture.Store
                (id fixture 41)
                service
                None
                "people.register"
                (Submissions.profile DomainInputs.actorProfile)

        let! registered = Service.executeSubmission owner registration token
        acceptedPeople registered

        let! granted =
            grant
                owner
                (id fixture 42)
                service
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.CaseId attachment.CaseId
                }
                true

        acceptedPeople granted

        let scopedReader = core fixture service
        let! scopedBytes = download scopedReader current
        assertDownload bytes scopedBytes
        let! beyondScope = Service.downloadAttachment scopedReader wrongParent token
        assertUnavailable beyondScope

        let! deniedWrite = upload scopedReader (add fixture 43 "service-duplicate.txt") bytes

        match deniedWrite.Outcome with
        | ExecuteResult.Rejected(Refusal.Access _) -> ()
        | other ->
            failtestf "A reader learned the duplicate through an unauthorized upload: %A" other

        let! revoked =
            grant
                owner
                (id fixture 44)
                service
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.CaseId attachment.CaseId
                }
                false

        acceptedPeople revoked
        let! revokedPreview = preview scopedReader current
        assertUnavailable revokedPreview

        let! stale = Service.executeSubmission owner staleErase token

        Expect.equal
            stale.Outcome
            (ExecuteResult.Rejected(Refusal.Case CaseRefusal.FamilyChanged))
            "Attachment acceptance invalidates an earlier erasure review"

        let! emptyReply = upload owner (empty fixture 23) [||]
        let emptyAttachment = accepted emptyReply |> attachmentReceipt
        let! emptyBytes = download owner emptyAttachment.AttachmentId
        assertDownload [||] emptyBytes

        let! initial = history owner None 1

        let continuation =
            (read initial).NextCursor
            |> Option.defaultWith (fun () -> failtest "Expected continuation")

        let withdrawal = withdraw fixture 24 current
        let! withdrawn = Service.executeSubmission owner withdrawal token

        Expect.equal
            ((accepted withdrawn |> attachmentReceipt).Snapshot)
            AttachmentDisclosure.Withheld
            "Withdrawal receipt suppresses the descriptor"

        let! unavailable = download owner current
        assertUnavailable unavailable
        let! withdrawnPreview = preview owner current
        assertUnavailable withdrawnPreview

        let! repeated = Service.executeSubmission owner (withdraw fixture 27 current) token

        Expect.equal
            repeated.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "A different withdrawal operation cannot withdraw the same ID"

        let! oldPage = history owner (Some continuation) 5

        match (read oldPage).Items with
        | [ entry ] ->
            Expect.equal
                entry.Subject.PartId
                (AttachmentId.value current)
                "The former upload remains structural evidence"

            Expect.equal
                entry.Changes
                TimelineChanges.Withheld
                "A page omitting withdrawal still redacts the older attachment"
        | other -> failtestf "Expected exactly the original structural attachment event: %A" other

        let! lookedUp = status owner submitted.OperationId

        Expect.equal
            lookedUp
            AttachmentDisclosure.Withheld
            "Fresh acceptance lookup redacts withdrawn metadata"

        let! retainedReview = Service.previewSubmission owner submitted token

        match read retainedReview.Outcome with
        | Previewed.AlreadyAccepted receipt ->
            Expect.equal
                (attachmentReceipt receipt).Snapshot
                AttachmentDisclosure.Withheld
                "Preview replay redacts the earlier receipt"
        | other -> failtestf "Expected preview replay: %A" other

        let! replay = upload owner submitted bytes

        Expect.equal
            (replayReceipt replay |> attachmentReceipt).Snapshot
            AttachmentDisclosure.Withheld
            "Byte-bearing replay also redacts the receipt"

        let! mismatch = upload owner submitted (Encoding.UTF8.GetBytes "abd")

        match mismatch.Outcome with
        | ExecuteResult.Rejected(Refusal.InvalidInput _) -> ()
        | other -> failtestf "Changed payload entered retry: %A" other

        let replacementBytes = Encoding.UTF8.GetBytes "independently valid replacement"
        let replacement = fromPayload fixture 89 replacementBytes

        let changed =
            OperationSubmission.admit
                { replacement.Envelope with
                    OperationId = submitted.OperationId
                }
            |> Result.defaultWith (fun error ->
                failtestf "Expected valid changed submission: %A" error)

        let! conflict = upload owner changed replacementBytes

        Expect.equal
            conflict.Outcome
            (ExecuteResult.Rejected Refusal.IdempotencyConflict)
            "Matching new bytes and a changed valid manifest conflict with the accepted intent"

        do!
            sql
                database
                "INSERT INTO claimcore.case_attachments(case_id,attachment_id) VALUES($1,$2)"
                [| box (CaseId.value attachment.CaseId); box (AttachmentId.value current) |]

        let! staleBytes = download owner current
        assertUnavailable staleBytes
        let! staleList = list owner

        Expect.equal
            ((read staleList).Items |> List.map _.AttachmentId)
            [ emptyAttachment.AttachmentId ]
            "A stale list row cannot disclose its descriptor"

        let! reattachedReply =
            upload owner (add fixture 25 "restored-under-new-id.bin") bytes

        let reattached = accepted reattachedReply |> attachmentReceipt
        Expect.notEqual reattached.AttachmentId current "Reattachment has a fresh identity"

        let! stillWithheld = status owner submitted.OperationId

        Expect.equal
            stillWithheld
            AttachmentDisclosure.Withheld
            "Reattachment cannot reopen an older ID"

        let! caseBeforeErase = Service.get owner { Target = target } token

        Expect.equal
            (read caseBeforeErase).Snapshot.Revision
            Revision.first
            "Attachment effects never advance case facts"

        let eraseCommand = erase fixture 26
        do! finding VerificationFinding.CaseAttachmentsProjection database

        do!
            sql
                database
                "DELETE FROM claimcore.case_attachments WHERE attachment_id=$1"
                [| box (AttachmentId.value current) |]

        let! trusted = checkpoint database

        do!
            sql
                database
                "DELETE FROM claimcore.case_attachments WHERE attachment_id=$1"
                [| box (AttachmentId.value reattached.AttachmentId) |]

        do! finding VerificationFinding.CaseAttachmentsProjection database
        let! handle = maintained database

        let! repaired =
            ProjectionRebuild.rebuild database.OwnerSource database.MasterKey trusted handle token

        Expect.isOk repaired "Projection-only damage is independently repairable"

        let! afterRepair = download owner reattached.AttachmentId
        assertDownload bytes afterRepair

        let! resumed =
            RegisterMaintenanceStore.change
                database.OwnerSource
                database.MasterKey
                (MaintenanceChange.Resume handle)
                token

        let! erasedReply = Service.executeSubmission owner eraseCommand token
        accepted erasedReply |> ignore

        let! erased = download owner reattached.AttachmentId
        assertUnavailable erased
        let! erasedPreview = preview owner reattached.AttachmentId
        assertUnavailable erasedPreview
        let! expiredIntent = upload owner submitted bytes

        Expect.equal
            expiredIntent.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "After erasure even an exact binary retry cannot compare private intent"

        let! evidence = status owner submitted.OperationId

        Expect.equal
            evidence
            AttachmentDisclosure.Erased
            "Erasure leaves structural acceptance with erased disclosure"

        for operation in [ submitted.OperationId; withdrawal.OperationId ] do
            let! lookup = Service.operationGet owner { OperationId = operation } token

            match read lookup.Outcome with
            | OperationLookupResult.Accepted(receipt, _) ->
                let document = ClaimCore.Presentation.HumanReceiptDocument.ofCore receipt

                Expect.equal
                    document.Disclosure
                    ClaimCore.Presentation.HumanDisclosure.Erased
                    "Authorized HUMAN lookup reports terminal attachment disclosure"

                Expect.equal
                    (document.AttachmentObservation
                     |> Option.map (fun observation -> observation.CaseId, observation.Sequence))
                    (Some(attachment.CaseId, receipt.Position.Sequence))
                    "Erased add and withdrawal receipts carry their typed attachment observation"
            | other -> failtestf "Expected authorized erased attachment receipt: %A" other

        use! evidenceConnection = database.OwnerSource.OpenConnectionAsync()

        let! disclosures =
            DatabaseSession.rows
                evidenceConnection
                "SELECT row_to_json(t)::text FROM claimcore.disclosure_trail t WHERE case_id=$1 AND attachment_id=$2 AND kind='ATTACHMENT_DOWNLOAD' AND outcome='SUCCEEDED'"
                [| box (CaseId.value attachment.CaseId); box (AttachmentId.value current) |]
                token

        Expect.isNonEmpty disclosures "Whole-case erasure preserves the real prior download event"

        for disclosure in disclosures do
            let text = DatabaseSession.value<string> 0 disclosure
            Expect.isFalse (text.Contains firstName) "The trail omits the first authored filename"

            Expect.isFalse
                (text.Contains "duplicate-name.txt")
                "The trail omits the second authored filename"

        let! erasedHead = checkpoint database
        let! erasedHandle = maintainedAt database resumed.State.Generation

        let! erasedRebuild =
            ProjectionRebuild.rebuild
                database.OwnerSource
                database.MasterKey
                erasedHead
                erasedHandle
                token

        Expect.isOk erasedRebuild "Accepted attachment history still rebuilds after erasure"
        return ()
    }

let private corruptWithdrawn (database: PostgresFixture.Database) =
    task {
        let! owner = setup database
        let fixture = database.Fixture
        let! added = upload owner (add fixture 21 firstName) bytes
        let attachment = accepted added |> attachmentReceipt

        let! withdrawn =
            Service.executeSubmission owner (withdraw fixture 22 attachment.AttachmentId) token

        accepted withdrawn |> ignore
        let! _ = verified database
        let! trusted = checkpoint database

        let! handle = maintained database
        // The privileged recovery connection owns this temporary snapshot.
        use! witness = database.SuperuserSource.OpenConnectionAsync()

        let! _ =
            DatabaseSession.execute
                witness
                "CREATE TEMP TABLE accepted_blob_fixture AS SELECT * FROM claimcore.attachment_blobs"
                [||]
                token

        let restoreFixture () =
            task {
                use! transaction = witness.BeginTransactionAsync(token)

                let! _ =
                    DatabaseSession.execute
                        witness
                        "SET LOCAL session_replication_role=replica;
                         DELETE FROM claimcore.attachment_blobs;
                         INSERT INTO claimcore.attachment_blobs SELECT * FROM accepted_blob_fixture"
                        [||]
                        token

                do! transaction.CommitAsync(token)
            }

        for statement in
            [
                "UPDATE claimcore.attachment_blobs SET payload=set_byte(payload,0,(get_byte(payload,0)+1)%256)"
                "DELETE FROM claimcore.attachment_blobs"
                "UPDATE claimcore.attachment_blobs SET case_id='10000000-0000-4000-8000-000000000007'"
                "INSERT INTO claimcore.attachment_blobs(case_id,blob_id,operation_sequence,effect_index)
                   SELECT case_id,'10000000-0000-4000-8000-000000000008',operation_sequence+1,49
                   FROM claimcore.attachment_blobs"
            ] do
            do! guardedSql database statement [||]
            do! finding VerificationFinding.AttachmentPayloads database

            let! rebuilt =
                ProjectionRebuild.rebuild
                    database.OwnerSource
                    database.MasterKey
                    trusted
                    handle
                    token

            let! admitted =
                RestoreAdmission.admit database.OwnerSource database.MasterKey trusted handle token

            for actual in [ rebuilt |> Result.map ignore; admitted |> Result.map ignore ] do
                match actual with
                | Error failure ->
                    Expect.equal
                        failure.Finding
                        VerificationFinding.AttachmentPayloads
                        "Maintenance paths share authoritative blob verification"
                | Ok() -> failtest "Corrupted withdrawn-only evidence passed maintenance admission"

            do! restoreFixture ()
            let! _ = verified database
            ()
    }

let private previewEligibility (database: PostgresFixture.Database) =
    task {
        let! owner = setup database
        let fixture = database.Fixture

        let png =
            Convert.FromBase64String
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=="

        let overProfile =
            Convert.FromBase64String
                "iVBORw0KGgoAAAANSUhEUgAAIAEAAAABCAYAAACZiUteAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=="

        // Complete baseline JPEG framing for a one-pixel, one-component image.
        let jpeg =
            Convert.FromHexString "FFD8FFC0000B080001000101011100FFDA0008010100003F0001FFD9"

        let cases =
            [
                71, "red.jpg", png, true, Some AttachmentPreview.Png
                72, "frame.JPEG", jpeg, true, Some AttachmentPreview.Jpeg
                73,
                "record.pdf",
                Encoding.ASCII.GetBytes "%PDF-1.7\n",
                true,
                Some AttachmentPreview.Pdf
                74, "fake.pdf", Encoding.ASCII.GetBytes "prefix before %PDF-1.7", true, None
                75, "fake.png", Encoding.ASCII.GetBytes "not a PNG", true, None
                76, "large.png", overProfile, true, None
                77, "neutral.txt", Encoding.ASCII.GetBytes "%PDF-2.0\n", false, None
            ]

        for number, name, payload, _, _ in cases do
            let command =
                draft
                    fixture
                    number
                    name
                    (payload.Length.ToString(CultureInfo.InvariantCulture))
                    (SHA256.HashData(payload) |> Convert.ToHexStringLower)

            let! reply = upload owner command payload
            accepted reply |> ignore

        let expectedOffers =
            cases |> List.map (fun (_, name, _, offered, _) -> name, offered) |> Map.ofList

        let assertOffers (result: Outcome<AttachmentPage>) =
            let offers =
                (read result).Items
                |> List.map (fun item -> AttachmentName.value item.Name, item.CanPreview)
                |> Map.ofList

            Expect.equal
                offers
                expectedOffers
                "Only canonical name suffixes govern list availability"

        let! listed = list owner
        assertOffers listed
        let page = read listed
        let document = ClaimCore.Presentation.HumanReadDocuments.attachments page

        Expect.equal
            (document.AttachmentObservation
             |> Option.map (fun observation -> observation.CaseId, observation.Sequence))
            (Some(page.CaseId, page.Disclosure))
            "The HUMAN list carries its typed attachment observation"

        let rows =
            document.Screen.Regions
            |> List.pick (fun region ->
                match region.Content with
                | ClaimCore.Presentation.RegionContent.Table table -> Some table.Rows
                | _ -> None)

        for number, name, _, offered, _ in cases do
            let id = attachmentGuid fixture number
            let row = rows |> List.find (fun row -> row.Key = AttachmentId.format id)

            Expect.equal
                (row.Transfers |> List.map (fun transfer -> transfer.Kind, transfer.LabelId))
                [
                    AttachmentRead.Download, "endpoint.attachment.download.label"
                    if offered then
                        AttachmentRead.Preview, "endpoint.attachment.preview.label"
                ]
                "HUMAN controls bind core-provided availability to the localized binary reads"

            for transfer in row.Transfers do
                Expect.equal (AttachmentName.value transfer.Name) name "Independent expected name"

                Expect.equal
                    transfer.Request.Target
                    (CaseSelector.Id page.CaseId)
                    "Bound case identity"

                Expect.equal transfer.Request.AttachmentId id "Bound attachment identity"

        for number, name, payload, _, expected in cases do
            let id = attachmentGuid fixture number
            let! proposed = preview owner id

            match expected, proposed with
            | Some kind, Outcome.Succeeded content ->
                Expect.equal content.Preview (Some kind) name
                assertDownload payload proposed
            | None, Outcome.Rejected Refusal.AttachmentPreviewUnavailable -> ()
            | _ -> failtestf "Unexpected preview for %s: %A" name proposed

            let! downloaded = download owner id
            assertDownload payload downloaded

        let! _ = verified database

        let pdf = attachmentGuid fixture 73

        do!
            guardedSql
                database
                "UPDATE claimcore.attachment_blobs SET payload=set_byte(payload,0,(get_byte(payload,0)+1)%256) WHERE blob_id=$1"
                [| box (AttachmentId.value pdf) |]

        do! finding VerificationFinding.AttachmentPayloads database

        let! withoutVerifiedPayload = list owner
        assertOffers withoutVerifiedPayload
        let! failed = preview owner pdf

        Expect.equal
            failed
            (Outcome.Failed CoreFault.StoreIntegrityError)
            "Reading the tampered blob fails"
    }

let private expectCapacity (reply: ExecuteReply) =
    Expect.equal
        reply.Outcome
        (ExecuteResult.Rejected Refusal.AttachmentCapacityReached)
        "Retention capacity is typed and consumes no operation"

let private slotCapacity (database: PostgresFixture.Database) =
    task {
        let! owner = setup database
        let fixture = database.Fixture

        for number in 0..98 do
            let payload = [| byte number |]
            let! added = upload owner (fromPayload fixture (100 + number) payload) payload
            accepted added |> ignore

        let candidate number value =
            let payload = [| value |]
            upload owner (fromPayload fixture number payload) payload

        let! raced = Task.WhenAll [| candidate 199 99uy; candidate 200 100uy |]

        let winners =
            raced
            |> Array.filter (fun reply ->
                match reply.Outcome with
                | ExecuteResult.Accepted _ -> true
                | _ -> false)

        Expect.hasLength winners 1 "Exactly one concurrent addition takes the 100th file slot"

        for rejected in
            raced |> Array.filter (fun reply -> reply.OperationId <> winners[0].OperationId) do
            expectCapacity rejected

        let! withdrawn =
            Service.executeSubmission
                owner
                (withdraw fixture 201 (attachmentGuid fixture 100))
                token

        accepted withdrawn |> ignore
        let! refused = candidate 202 101uy
        expectCapacity refused

        let beyondQuota = [| 101uy |]

        let! blocked =
            upload
                owner
                (draft
                    fixture
                    205
                    "not-capacity.exe"
                    "1"
                    (SHA256.HashData beyondQuota |> Convert.ToHexStringLower))
                beyondQuota

        expectBlocked blocked

        let reattachment = fromPayload fixture 203 [| 0uy |]

        for draft in [ reattachment; fromPayload fixture 204 [| 101uy |] ] do
            let! preview = Service.previewSubmission owner draft token

            match preview.Outcome with
            | Outcome.Succeeded(Previewed.Reviewed _) -> ()
            | other ->
                failtestf "Metadata review exposes retention membership at capacity: %A" other

        let! reattached = upload owner reattachment [| 0uy |]
        accepted reattached |> ignore
        let! current = list owner

        Expect.hasLength
            (read current).Items
            100
            "Reattachment reuses the retired blob's final slot"
    }

let private byteCapacity (database: PostgresFixture.Database) =
    task {
        let! owner = setup database
        let fixture = database.Fixture
        let oversized = Array.zeroCreate<byte> 26_214_401
        let! tooLarge = upload owner (fromPayload fixture 90 oversized) oversized

        match tooLarge.Outcome with
        | ExecuteResult.Rejected(Refusal.InvalidInput _) -> ()
        | other -> failtestf "A file one byte over the cap did not refuse: %A" other

        let exact = Array.zeroCreate<byte> 26_214_400

        for number in 0..9 do
            exact[0] <- byte number
            let! added = upload owner (fromPayload fixture (100 + number) exact) exact
            accepted added |> ignore

        let! retrieved = download owner (attachmentGuid fixture 109)

        Expect.isTrue
            ((VerifiedAttachment.bytes ((read retrieved).Bytes))
                .SequenceEqual(ReadOnlySpan<byte> exact))
            "The 25 MiB boundary preserves exact bytes"

        let final = Array.zeroCreate<byte> 6_291_456
        let! atCap = upload owner (fromPayload fixture 111 final) final
        accepted atCap |> ignore
        let extra = [| 42uy |]

        let addExtra number =
            upload owner (fromPayload fixture number extra) extra

        let! tooMuch = addExtra 112
        expectCapacity tooMuch

        let! withdrawn =
            Service.executeSubmission
                owner
                (withdraw fixture 113 (attachmentGuid fixture 111))
                token

        accepted withdrawn |> ignore
        let! stillFull = addExtra 114
        expectCapacity stillFull

        let! reattached = upload owner (fromPayload fixture 115 final) final
        accepted reattached |> ignore

        let! tally =
            database.Fixture.Store.Read
                {
                    CancellationToken = token
                    Work =
                        fun tx ->
                            AttachmentStorage.usage
                                tx
                                (CaseId.ofOperationId (
                                    OperationId.create (id fixture 10) |> Option.get
                                ))
                }

        Expect.equal
            tally
            (Ok(11L, 268_435_456L))
            "Reattachment does not grow the distinct bytes at exactly 256 MiB"

        let! _ = verified database
        return ()

    }

// Pause after the application has verified the bytes, before the real store takes
// the ledger head lock. Each test changes exactly one admission fact in that gap.
let private afterVerifiedBytes owner command change =
    task {
        let reached =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let resume =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let waiting =
            owner
            |> Service.withWriteAdmission (fun cancellation ->
                task {
                    reached.SetResult()
                    do! resume.Task.WaitAsync cancellation

                    return
                        { new IDisposable with
                            member _.Dispose() = ()
                        }
                })

        let pending = upload waiting command bytes

        try
            do! reached.Task.WaitAsync(TimeSpan.FromSeconds 15.)
            do! change ()
        finally
            resume.TrySetResult() |> ignore

        return! pending.WaitAsync(TimeSpan.FromSeconds 15.)
    }

let private reviewed owner command =
    task {
        let! preview = Service.previewSubmission owner command token

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed _) -> ()
        | other -> failtestf "Expected a successful upload review, got %A" other
    }

let private changesBeforeAdmission requester act expected (database: PostgresFixture.Database) =
    task {
        let! owner = setup database
        let fixture = database.Fixture
        let! writer = requester fixture owner
        let command = add fixture 80 "reviewed-transfer.txt"
        do! reviewed writer command
        let! result = afterVerifiedBytes writer command (fun () -> act database owner writer)

        Expect.equal
            result.Outcome
            (ExecuteResult.Rejected expected)
            "The locked decision refused the verified transfer using the new facts"

        let! lookup = Service.operationGet owner { OperationId = command.OperationId } token

        match lookup.Outcome with
        | Outcome.Succeeded(OperationLookupResult.Pending _) -> ()
        | other -> failtestf "The refused upload has no accepted operation: %A" other

        let! count =
            DatabaseSession.read database.OwnerSource token (fun connection ->
                DatabaseSession.rows
                    connection
                    "SELECT count(*) FROM claimcore.attachment_blobs WHERE blob_id=$1"
                    [| box command.OperationId |]
                    token)

        Expect.equal (DatabaseSession.value<int64> 0 count.Head) 0L "No payload survives refusal"
    }

let private sameRequester _ owner = Task.FromResult owner

let private caseHandler fixture owner =
    task {
        let principal =
            PrincipalKey.human "https://issuer.example/" "attachment-handler"
            |> Result.defaultWith (string >> invalidOp)

        let! registration =
            personSubmission
                fixture.Store
                (id fixture 81)
                principal
                None
                "people.register"
                (Submissions.profile DomainInputs.actorProfile)

        let! reply = Service.executeSubmission owner registration token
        acceptedPeople reply

        for number, role in [ 82, Role.CaseReader; 83, Role.CaseHandler ] do
            let! reply =
                grant
                    owner
                    (id fixture number)
                    principal
                    {
                        Role = role
                        Scope = GrantTarget.Installation
                    }
                    true

            acceptedPeople reply

        return core fixture principal
    }

let private revoke (database: PostgresFixture.Database) owner (writer: ServiceContext) =
    task {
        // Another writer can attach the same bytes after this review and before locked admission.
        let! seed = upload owner (add database.Fixture 85 "initial-file.txt") bytes
        accepted seed |> ignore

        let! reply =
            grant
                owner
                (id database.Fixture 84)
                writer.Principal
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.Installation
                }
                false

        acceptedPeople reply
    }

let private voidCase (database: PostgresFixture.Database) owner _ =
    task {
        let reason =
            Map.ofList
                [
                    Submissions.input InputTarget.Reason,
                    SubmissionValue.Text "Synthetic lifecycle change"
                ]

        let! reply =
            Service.executeSubmission
                owner
                (request database.Fixture 84 "synthetic-a" 1L "case.void" reason)
                token

        accepted reply |> ignore
    }

let private eraseCase (database: PostgresFixture.Database) owner _ =
    task {
        let! reply = Service.executeSubmission owner (erase database.Fixture 84) token
        accepted reply |> ignore
    }

let private expiryBeforeAdmission (database: PostgresFixture.Database) =
    task {
        let! owner = setup database
        let command = add database.Fixture 80 "expires-in-flight.txt"
        do! reviewed owner command

        let operation = OperationId.create command.OperationId |> Option.get
        let expired = (OperationLifetime.finalAt operation).AddSeconds 1.

        let future =
            ClaimCore.IntegrationTests.OperationLookupTests.ControlledStore(
                owner.Store,
                Some expired,
                None,
                None,
                None,
                readInstant = expired
            )

        let later = Service.mapStore (fun _ -> future) owner

        let! unconfirmed = afterVerifiedBytes later command (fun () -> Task.FromResult())

        Expect.equal
            unconfirmed.Outcome
            (ExecuteResult.Rejected(
                Refusal.OperationIdOutOfWindow(OperationId.issuedAt operation, expired)
            ))
            "The locked execution instant enforces expiry before any absence confirmation"

        let! late =
            afterVerifiedBytes later command (fun () ->
                task {
                    let! lookup =
                        Service.operationGet later { OperationId = command.OperationId } token

                    Expect.equal
                        lookup.Outcome
                        (Outcome.Succeeded OperationLookupResult.NotAccepted)
                        "A separate locked lookup settles final absence while the transfer waits"
                })

        match late.Outcome with
        | ExecuteResult.Rejected(Refusal.OperationClockUnavailable _) -> ()
        | other -> failtestf "The delayed transfer cannot revive a settled operation: %A" other

        let! lookup = Service.operationGet owner { OperationId = command.OperationId } token

        Expect.equal
            lookup.Outcome
            (Outcome.Succeeded OperationLookupResult.NotAccepted)
            "The real clock still observes durable final absence"
    }

let private readAcrossWithdrawal (database: PostgresFixture.Database) =
    task {
        let! owner = setup database
        let! reply = upload owner (add database.Fixture 80 firstName) bytes
        let attachment = (accepted reply |> attachmentReceipt).AttachmentId
        let inner = owner.Store

        let pinned =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let resume =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let delayed =
            owner
            |> Service.mapStore (fun _ ->
                { new IStore with
                    member _.RecordDisclosure(event, token) = inner.RecordDisclosure(event, token)

                    member _.Context query = inner.Context query
                    member _.Write operation = inner.Write operation

                    member _.Read operation =
                        inner.Read
                            { operation with
                                Work =
                                    fun tx ->
                                        task {
                                            let! _ =
                                                OperationStorage.position
                                                    tx.Connection
                                                    tx.CancellationToken

                                            pinned.SetResult()
                                            do! resume.Task.WaitAsync(tx.CancellationToken)
                                            return! operation.Work tx
                                        }
                            }
                })

        let old = download delayed attachment

        try
            do! pinned.Task.WaitAsync(TimeSpan.FromSeconds 15.)

            let! withdrawn =
                Service.executeSubmission owner (withdraw database.Fixture 81 attachment) token

            accepted withdrawn |> ignore
            let! fresh = download owner attachment
            assertUnavailable fresh
        finally
            resume.TrySetResult() |> ignore

        let! earlier = old.WaitAsync(TimeSpan.FromSeconds 15.)
        assertDownload bytes earlier

        let! fresh = download owner attachment
        assertUnavailable fresh
    }

let private encryptedRows (database: PostgresFixture.Database) =
    task {
        let! owner = setup database
        let fixture = database.Fixture
        let bytes = Encoding.UTF8.GetBytes "synthetic attachment plaintext witness"
        let name = "synthetic-private-filename.txt"
        let digest = SHA256.HashData bytes

        let uploadTo selector number =
            let command =
                draftFor
                    selector
                    fixture
                    number
                    name
                    (bytes.Length.ToString(CultureInfo.InvariantCulture))
                    (Convert.ToHexStringLower digest)

            upload owner command bytes

        let! opened =
            Service.executeSubmission
                owner
                (request
                    fixture
                    70
                    "encrypted-second-case"
                    0L
                    "case.open"
                    (Submissions.fields (RegistrationInput.values registration)))
                token

        accepted opened |> ignore
        let! first = uploadTo target 71
        let first = accepted first |> attachmentReceipt
        let! second = uploadTo (CaseSelector.Reference "encrypted-second-case") 72
        let second = accepted second |> attachmentReceipt
        let! downloaded = download owner first.AttachmentId
        assertDownload bytes downloaded
        let! _ = verified database

        use! connection = database.OwnerSource.OpenConnectionAsync()

        let stored id =
            task {
                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT b.payload,b.digest,b.operation_sequence,c.ordinal,c.payload FROM claimcore.attachment_blobs b JOIN claimcore.ledger_compartments c USING(operation_sequence) WHERE b.blob_id=$1"
                        [| box (AttachmentId.value id) |]
                        token

                return List.exactlyOne rows
            }

        let! raw = stored first.AttachmentId
        let! other = stored second.AttachmentId
        let blob = DatabaseSession.value<byte array> 0 raw
        let keyed = DatabaseSession.value<byte array> 1 raw
        let sequence = DatabaseSession.value<int64> 2 raw
        let ordinal = DatabaseSession.value<int> 3 raw
        let compartment = DatabaseSession.value<byte array> 4 raw
        let fileName = Encoding.UTF8.GetBytes name

        let has (needle: byte array) (value: byte array) =
            (ReadOnlySpan<byte> value).IndexOf(ReadOnlySpan<byte> needle) >= 0

        let caseId = CaseId.value first.CaseId
        let blobId = AttachmentId.value first.AttachmentId
        let otherCaseId = CaseId.value second.CaseId
        let otherBlobId = AttachmentId.value second.AttachmentId

        Expect.equal blob.Length (bytes.Length + 28) "Blob framing includes one IV and one tag"
        Expect.isFalse (has bytes blob) "The blob row keeps file content private"
        Expect.isFalse (keyed = digest) "The duplicate index has no ordinary SHA-256"
        Expect.isFalse (has fileName compartment) "The compartment keeps the filename private"

        let clear =
            database.Cipher.DecryptCompartment(sequence, ordinal, "FAMILY", caseId, compartment)

        Expect.equal
            compartment.Length
            (clear.Length + 28)
            "Compartment framing includes an IV and tag"

        Expect.isTrue
            (has fileName clear)
            "The encrypted value contains the expected retained detail"

        let tryBlob family id () =
            database.Cipher.DecryptBlob(family, id, blob) |> ignore

        Expect.throwsT<RecordCorruptException>
            (tryBlob caseId otherBlobId)
            "Changing only the blob ID breaks authentication"

        Expect.throwsT<RecordCorruptException>
            (tryBlob otherCaseId blobId)
            "Changing only the case ID breaks authentication"

        Expect.throwsT<RecordCorruptException>
            (fun () ->
                database.Cipher.DecryptCompartment(
                    sequence + 1L,
                    ordinal,
                    "FAMILY",
                    caseId,
                    compartment
                )
                |> ignore)
            "Moving a compartment to the next row breaks authentication"

        let replaceBlob payload =
            guardedSql
                database
                "UPDATE claimcore.attachment_blobs SET payload=$2 WHERE blob_id=$1"
                [| box blobId; box payload |]

        do! replaceBlob (DatabaseSession.value<byte array> 0 other)
        do! finding VerificationFinding.AttachmentPayloads database
        do! replaceBlob blob

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger_compartments SET payload=$3 WHERE operation_sequence=$1 AND ordinal=$2"
                [| box sequence; box ordinal; box (DatabaseSession.value<byte array> 4 other) |]

        do! finding VerificationFinding.PayloadFormat database
    }

[<Tests>]
let tests =
    testList
        "PostgreSQL attachment policy"
        [
            testCaseAsync
                "attachment and compartment ciphertext are private and bound to their rows"
                (run encryptedRows)
            testCaseAsync
                "verified upload rechecks revoked grant"
                (run (
                    changesBeforeAdmission
                        caseHandler
                        revoke
                        (Refusal.Access(AccessRefusal.NotAuthorized Capability.ManageCaseWork))
                ))
            testCaseAsync
                "verified upload rechecks maintenance"
                (run (
                    changesBeforeAdmission
                        sameRequester
                        (fun database _ _ -> enterMaintenance database)
                        Refusal.RegisterMaintenance
                ))
            testCaseAsync
                "verified upload rechecks void"
                (run (
                    changesBeforeAdmission
                        sameRequester
                        voidCase
                        (Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Voided))
                ))
            testCaseAsync
                "verified upload rechecks erasure"
                (run (
                    changesBeforeAdmission
                        sameRequester
                        eraseCase
                        (Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Erased))
                ))
            testCaseAsync
                "verified upload rechecks expiry and final absence"
                (run expiryBeforeAdmission)
            testCaseAsync
                "attachment download snapshots straddle withdrawal"
                (run readAcrossWithdrawal)
            testCaseAsync "acceptance, withdrawal, old disclosure and list rebuild" (run lifecycle)
            testCaseAsync
                "preview availability uses names and verified bytes at their respective read boundaries"
                (run previewEligibility)
            testCaseAsync
                "withdrawn-only bytes remain authoritative for verify, rebuild and restore"
                (run corruptWithdrawn)
            testCaseAsync
                "retained attachment file slots under concurrent admission"
                (run slotCapacity)
            testCaseAsync
                "retained attachment bytes at both inclusive size limits"
                (run byteCapacity)
        ]
