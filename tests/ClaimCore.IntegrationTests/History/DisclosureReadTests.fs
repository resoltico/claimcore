module ClaimCore.IntegrationTests.DisclosureReadTests

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.Wire
open Expecto

let private token = CancellationToken.None
let private target = AttachmentPostgresTests.target

let private selection: DisclosureTrailRequest =
    {
        ActorId = None
        CaseId = None
        Kind = None
        From = None
        Until = None
        AfterCursor = None
        Limit = 50
    }

let private success =
    function
    | Outcome.Succeeded value -> value
    | Outcome.Rejected refusal -> failtestf "Unexpected refusal: %A" refusal
    | Outcome.Failed fault -> failtestf "Unexpected store fault: %A" fault

let private expectUnavailable =
    function
    | Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable) -> ()
    | _ -> failtest "The target must remain opaque."

let private expectStoreFailure =
    function
    | Outcome.Failed CoreFault.StoreUnavailable -> ()
    | _ -> failtest "An unaudited read must return a store fault before releasing content."

let private allEvents (fixture: Fixture) =
    task {
        let! result =
            fixture.Store.Read
                {
                    CancellationToken = token
                    Work = fun tx -> DisclosureStorage.page tx selection None selection.Limit
                }

        return result |> Result.defaultWith (fun _ -> failtest "Could not inspect the trail.")
    }

let private timeline services target =
    Service.timeline
        services
        {
            Target = target
            Filter = None
            AfterCursor = None
            Limit = 50
        }
        token

let private grantAuditor fixture owner number scope =
    task {
        let! granted =
            grant
                owner
                (id fixture number)
                fixture.Owner
                { Role = Role.Auditor; Scope = scope }
                true

        acceptedPeople granted
    }

let private upload (fixture: Fixture) owner =
    task {
        let bytes = Encoding.ASCII.GetBytes "%PDF-1.7\n"
        let number = 71
        let digest = SHA256.HashData(bytes) |> Convert.ToHexStringLower

        let command =
            Submissions.createRaw
                "attachment.add"
                (id fixture number)
                (OperationBinding.Attachment(target, None))
                ([
                    InputTarget.AttachmentName, "synthetic-disclosure-file.pdf"
                    InputTarget.AttachmentLength,
                    bytes.Length.ToString(CultureInfo.InvariantCulture)
                    InputTarget.AttachmentDigest, digest
                 ]
                 |> List.map (fun (key, value) -> Submissions.text (Submissions.input key) value)
                 |> Map.ofList)

        let! written = AttachmentPostgresTests.upload owner command bytes
        accepted written |> ignore
        return AttachmentPostgresTests.attachmentGuid fixture number
    }

let private attachmentRequest attachment : AttachmentReadRequest =
    {
        Target = target
        AttachmentId = attachment
    }

let private disclosureReads (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = VerificationSupport.populated database
        let! attachment = upload fixture owner
        let missing = AttachmentPostgresTests.attachmentGuid fixture 72
        let! dossier = Service.get owner { Target = target } token
        let caseId = (success dossier).Snapshot.CaseId
        let! history = timeline owner target
        success history |> ignore
        let! receipt = Service.operationGet owner { OperationId = id fixture 10 } token

        match success receipt.Outcome with
        | OperationLookupResult.Accepted _ -> ()
        | _ -> failtest "The expected case receipt was not returned."

        // Both calls go through the transfer verification path.
        let! downloaded =
            Service.downloadAttachment owner (attachmentRequest attachment) token

        let! previewed = Service.previewAttachment owner (attachmentRequest attachment) token
        Expect.isNone (success downloaded).Preview "Ordinary download"
        Expect.equal (success previewed).Preview (Some AttachmentPreview.Pdf) "Verified PDF preview"
        let! denied = AttachmentPostgresTests.download owner missing
        expectUnavailable denied

        let! attachments = AttachmentPostgresTests.list owner
        success attachments |> ignore

        for text in [ None; Some "synthetic" ] do
            let! page =
                Service.list
                    owner
                    {
                        Filter = CaseListFilter.create [] [] [] text CaseListOrder.Reference
                        Page = { AfterCursor = None; Limit = 5 }
                    }
                    token

            success page |> ignore

        let! exported =
            Service.timelineForExport
                owner
                {
                    Target = target
                    Filter = None
                    AfterCursor = None
                    Limit = 50
                }
                token

        success exported |> ignore
        let! actor = inspectPeople fixture fixture.Owner token

        let actorId =
            match actor with
            | Ok { Actor = Some actor } -> actor.ActorId
            | _ -> failtest "The fixture case worker must have an ActorId."

        let! actual = allEvents fixture

        let observed =
            actual
            |> List.map (fun row ->
                Expect.equal
                    row.Event.ActorId
                    actorId
                    "Use the authenticated actor in the read snapshot"

                Expect.equal
                    row.Event.CaseId
                    (Some caseId)
                    "Use the case ID resolved in the read snapshot"

                Expect.isNone row.Event.Export "Case and attachment reads have no export data"
                row.Event.Kind, row.Event.Outcome, row.Event.AttachmentId)
            |> List.sort

        let expected =
            [
                DisclosureKind.CaseRead, DisclosureOutcome.Succeeded, None
                DisclosureKind.CaseRead, DisclosureOutcome.Succeeded, None
                DisclosureKind.CaseRead, DisclosureOutcome.Succeeded, None
                DisclosureKind.AttachmentDownload, DisclosureOutcome.Succeeded, Some attachment
                DisclosureKind.AttachmentPreview, DisclosureOutcome.Succeeded, Some attachment
                DisclosureKind.AccessRefused, DisclosureOutcome.Refused, Some missing
            ]
            |> List.sort

        Expect.equal
            observed
            expected
            "Each disclosure records once and lists and searches add no rows"

        let! denied = Service.disclosureTrail owner selection token
        expectUnavailable denied
        do! grantAuditor fixture owner 73 (GrantTarget.CaseId caseId)
        let! scoped = Service.disclosureTrail owner selection token
        expectUnavailable scoped
        do! grantAuditor fixture owner 74 GrantTarget.Installation

        let from = actual |> List.last |> _.RecordedAt
        let until = (actual |> List.head |> _.RecordedAt).AddTicks(10L)

        let filtered =
            { selection with
                ActorId = Some actorId
                CaseId = Some caseId
                Kind = Some DisclosureKind.CaseRead
                From = Some from
                Until = Some until
                Limit = 2
            }

        let! first = Service.disclosureTrail owner filtered token
        let first = success first
        Expect.equal first.Items.Length 2 "Apply the requested page size"
        Expect.isSome first.NextCursor "A remaining event has a continuation"

        let! second =
            Service.disclosureTrail
                owner
                { filtered with
                    AfterCursor = first.NextCursor
                }
                token

        let second = success second
        Expect.isNone second.NextCursor "The second page exhausts this selection"

        Expect.equal
            (List.append first.Items second.Items)
            (actual |> List.filter (fun row -> row.Event.Kind = DisclosureKind.CaseRead))
            "Actor, case, kind and time filters keep precisely the selected events through paging"

        let! wrongFilter =
            Service.disclosureTrail
                owner
                { filtered with
                    Kind = None
                    AfterCursor = first.NextCursor
                }
                token

        match wrongFilter with
        | Outcome.Rejected Refusal.InvalidCursor -> ()
        | _ -> failtest "A continuation cannot be transplanted to a different selection."

        let! final = allEvents fixture

        let trailEvents =
            final
            |> List.filter (fun row -> row.Event.Kind = DisclosureKind.TrailRead)
            |> List.map _.Event

        let trailEvent =
            {
                DisclosureEvent.ActorId = actorId
                Kind = DisclosureKind.TrailRead
                CaseId = None
                AttachmentId = None
                Outcome = DisclosureOutcome.Succeeded
                Export = None
            }

        Expect.equal
            trailEvents
            [ trailEvent; trailEvent ]
            "Each released page records a trail read"
    }

let private failingAudit beforeAttempt services =
    services
    |> Service.mapStore (fun store ->
        { new IStore with
            member _.Context operation = store.Context operation
            member _.Read operation = store.Read operation
            member _.Write operation = store.Write operation

            member _.RecordDisclosure(_, _) =
                beforeAttempt ()
                Task.FromResult(Error StoreFailure.Unavailable)
        })

let private failClosed (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = VerificationSupport.populated database
        let! attachment = upload fixture owner
        do! grantAuditor fixture owner 73 GrantTarget.Installation
        let mutable attempts = 0

        let failing =
            failingAudit (fun () -> Interlocked.Increment(&attempts) |> ignore) owner

        let! dossier = Service.get failing { Target = target } token
        expectStoreFailure dossier
        let! history = timeline failing target
        expectStoreFailure history
        let! receipt = Service.operationGet failing { OperationId = id fixture 10 } token
        expectStoreFailure receipt.Outcome

        let! downloaded =
            Service.downloadAttachment failing (attachmentRequest attachment) token

        expectStoreFailure downloaded

        let! previewed =
            Service.previewAttachment failing (attachmentRequest attachment) token

        expectStoreFailure previewed
        let! trail = Service.disclosureTrail failing selection token
        expectStoreFailure trail
        Expect.equal attempts 6 "Each read attempts the separate audit write before its result"
        let! actual = allEvents fixture
        Expect.isEmpty actual "The injected audit failure wrote no rows"
    }

let private postgresFailureAtHttpBoundary () =
    async {
        let principal =
            PrincipalKey.human TestServerFixture.oidc.Issuer.AbsoluteUri "synthetic-human"
            |> Result.defaultWith (fun _ -> failtest "Synthetic principal")

        let! database = PostgresFixture.createFor principal |> Async.AwaitTask

        try
            do!
                task {
                    let fixture = database.Fixture
                    let! owner = VerificationSupport.populated database
                    let! attachment = upload fixture owner

                    use host =
                        new TestServerFixture.Host(
                            true,
                            installationId = fixture.InstallationId,
                            forActor = core fixture
                        )

                    let! _ = host.Login()
                    let! credential = host.Token()
                    let caseBody = Codec.encode Api.caseGet.Input { Target = target }

                    let attachmentBody =
                        Codec.encode AttachmentTransport.request (attachmentRequest attachment)

                    let caseFault =
                        Codec.encode
                            Api.caseGet.Output
                            (RepresentationReply.Data(Outcome.Failed CoreFault.SchemaMismatch))

                    let transferFault =
                        Codec.encode
                            AttachmentTransport.failure
                            (AttachmentTransferFailure.Failed CoreFault.SchemaMismatch)

                    let requests =
                        [
                            "case.get", caseBody, "application/json", caseFault
                            "attachment.download",
                            attachmentBody,
                            "application/octet-stream",
                            transferFault
                            "attachment.preview", attachmentBody, "application/pdf", transferFault
                        ]

                    let call endpoint body =
                        host.CallResponse(
                            endpoint,
                            body,
                            credential,
                            representation = Representation.Data
                        )

                    for endpoint, body, media, _ in requests do
                        use! response = call endpoint body
                        let! bytes = response.Content.ReadAsByteArrayAsync()

                        Expect.equal
                            (int response.StatusCode)
                            200
                            (endpoint + " can release content")

                        Expect.equal
                            (nonNull response.Content.Headers.ContentType).MediaType
                            media
                            endpoint

                        if endpoint = "case.get" then
                            match
                                Codec.decode Limits.MaximumResponseBytes Api.caseGet.Output bytes
                            with
                            | Ok(RepresentationReply.Data(Outcome.Succeeded _)) -> ()
                            | _ ->
                                failtest "The case read must succeed before the fault is injected."
                        else
                            Expect.sequenceEqual
                                bytes
                                (Encoding.ASCII.GetBytes "%PDF-1.7\n")
                                endpoint

                    let! before = allEvents fixture
                    Expect.equal before.Length 3 "The positive HTTP reads each record a disclosure"

                    use! connection = database.OwnerSource.OpenConnectionAsync(token)

                    let! _ =
                        DatabaseSession.execute
                            connection
                            "ALTER TABLE claimcore.disclosure_trail RENAME TO disclosure_trail_unavailable"
                            [||]
                            token

                    try
                        for endpoint, body, _, fault in requests do
                            use! response = call endpoint body
                            let! bytes = response.Content.ReadAsByteArrayAsync()

                            Expect.equal
                                (nonNull response.Content.Headers.ContentType).MediaType
                                "application/json"
                                (endpoint + " uses the failure media type")

                            Expect.isNull response.Content.Headers.ContentDisposition endpoint
                            Expect.isFalse (response.Headers.Contains "Content-Digest") endpoint

                            Expect.sequenceEqual
                                bytes
                                fault
                                (endpoint + " releases only the typed store fault")
                    finally
                        DatabaseSession.execute
                            connection
                            "ALTER TABLE claimcore.disclosure_trail_unavailable RENAME TO disclosure_trail"
                            [||]
                            token
                        |> _.GetAwaiter().GetResult()
                        |> ignore

                    let! after = allEvents fixture
                    Expect.equal after before "The failed PostgreSQL inserts added no events"
                }
                |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

let private retainedReplies (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = VerificationSupport.populated database
        let original = ApplicationFixtures.openRequest fixture 10 "synthetic-a"

        let amend number policyNumber =
            request
                fixture
                number
                "synthetic-a"
                1L
                "case.amendRegistration"
                (Submissions.updates (
                    RegistrationUpdateInput.values (
                        DomainInputs.registrationUpdate (
                            DomainInputs.registrationWith
                                [ BusinessField.PolicyNumber, policyNumber ]
                                DomainInputs.registration
                        )
                    )
                ))

        let! revised =
            Service.executeSubmission owner (amend 71 "Retained synthetic value") token

        let caseId = (accepted revised |> caseReceipt).CaseId
        let stale = amend 72 "Stale synthetic value"

        let history =
            function
            | Refusal.RevisionConflict conflict ->
                match conflict.Changes with
                | Some(ConflictChanges.Page page) ->
                    Expect.isNonEmpty page.Entries "The response contains retained case history"
                | _ -> failtest "Expected the visible conflict history page."
            | _ -> failtest "Expected a revision conflict."

        let! preview = Service.previewSubmission owner original token

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.AlreadyAccepted _) -> ()
        | _ -> failtest "The preview must return the previously accepted case receipt."

        let! replay = Service.executeSubmission owner original token
        replayReceipt replay |> ignore
        let! preview = Service.previewSubmission owner stale token

        match preview.Outcome with
        | Outcome.Rejected refusal -> history refusal
        | _ -> failtest "Expected a stale preview with conflict history."

        let! revisionConflict = Service.executeSubmission owner stale token

        match revisionConflict.Outcome with
        | ExecuteResult.Rejected refusal -> history refusal
        | _ -> failtest "Expected a stale execution with conflict history."

        let! audited = allEvents fixture
        Expect.hasLength audited 4 "Each replay or conflict page adds one row"

        for entry in audited do
            Expect.equal entry.Event.CaseId (Some caseId) "Record the retained case identity"
            Expect.equal entry.Event.Kind DisclosureKind.CaseRead "Treat retained history as a read"

            Expect.equal
                entry.Event.Outcome
                DisclosureOutcome.Succeeded
                "Attests to released history"

        let failing = failingAudit ignore owner

        for operation in [ original; stale ] do
            let! preview = Service.previewSubmission failing operation token
            expectStoreFailure preview.Outcome
            let! execution = Service.executeSubmission failing operation token

            match execution.Outcome with
            | ExecuteResult.Failed CoreFault.StoreUnavailable -> ()
            | _ -> failtest "Execution must withhold an unaudited replay or conflict page."
    }

[<Tests>]
let tests =
    testList
        "Disclosure read behavior"
        [
            testCaseAsync
                "case and attachment disclosures are exact and the trail is Auditor-only"
                (ApplicationFixtures.run (fun database _ -> disclosureReads database))
            testCaseAsync
                "an audit write failure blocks every case and attachment result"
                (ApplicationFixtures.run (fun database _ -> failClosed database))
            testCaseAsync
                "PostgreSQL disclosure insert failure withholds case, download and preview HTTP content"
                (postgresFailureAtHttpBoundary ())
            testCaseAsync
                "receipt replays and revision conflicts audit retained disclosures"
                (ApplicationFixtures.run (fun database _ -> retainedReplies database))
        ]
