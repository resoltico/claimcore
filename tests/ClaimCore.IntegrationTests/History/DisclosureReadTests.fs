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
let private target fixture = caseTarget fixture "synthetic-a"

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
                    Work =
                        fun tx ->
                            task {
                                let! bound = DisclosureStorage.upperBound tx

                                return!
                                    DisclosureStorage.page tx selection None bound selection.Limit
                            }
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
                (OperationBinding.Attachment(target fixture, None))
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

let private attachmentRequest fixture attachment : AttachmentReadRequest =
    {
        Target = target fixture
        AttachmentId = attachment
    }

let private disclosureReads (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = VerificationSupport.populated database
        let! attachment = upload fixture owner
        let! setup = allEvents fixture
        let missing = AttachmentPostgresTests.attachmentGuid fixture 72
        let! dossier = Service.get owner { Target = target fixture } token
        let caseId = (success dossier).Snapshot.CaseId
        let! history = timeline owner (target fixture)
        let history = success history
        let! detailAgain = Service.get owner { Target = target fixture } token
        success detailAgain |> ignore
        let! receipt = Service.operationGet owner { OperationId = id fixture 10 } token

        match success receipt.Outcome with
        | OperationLookupResult.Accepted _ -> ()
        | _ -> failtest "The expected case receipt was not returned."

        // Both calls go through the transfer verification path.
        let! downloaded =
            Service.downloadAttachment owner (attachmentRequest fixture attachment) token

        let! previewed =
            Service.previewAttachment owner (attachmentRequest fixture attachment) token

        Expect.isNone (success downloaded).Preview "Ordinary download"
        Expect.equal (success previewed).Preview (Some AttachmentPreview.Pdf) "Verified PDF preview"

        let! denied =
            Service.downloadAttachment owner (attachmentRequest fixture missing) token

        expectUnavailable denied

        let! attachments = Service.caseAttachments owner (target fixture) token
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
                    Target = target fixture
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
        let actual = actual |> List.filter (fun row -> not (List.contains row setup))

        let observed =
            actual
            |> List.map (fun row ->
                Expect.equal
                    row.Event.ActorId
                    actorId
                    "Use the authenticated actor in the read snapshot"

                Expect.equal
                    row.Event.CaseId
                    (match row.Event.Kind with
                     | DisclosureKind.AttachmentDownload when
                         row.Event.Outcome = DisclosureOutcome.Refused
                         ->
                         None
                     | DisclosureKind.CaseList -> None
                     | _ -> Some caseId)
                    "Disclosed direct observations use a case ID and opaque refusals omit it"

                Expect.isNone row.Event.Export "Case and attachment reads have no export data"
                row.Event.Kind, row.Event.Outcome, row.Event.AttachmentId)
            |> List.sort

        let expected =
            [
                DisclosureKind.OperationLookup, DisclosureOutcome.Succeeded, None
                DisclosureKind.CaseTimeline, DisclosureOutcome.Succeeded, None
                DisclosureKind.CaseRead, DisclosureOutcome.Succeeded, None
                DisclosureKind.CaseRead, DisclosureOutcome.Succeeded, None
                DisclosureKind.AttachmentDownload, DisclosureOutcome.Succeeded, Some attachment
                DisclosureKind.AttachmentPreview, DisclosureOutcome.Succeeded, Some attachment
                DisclosureKind.AttachmentDownload, DisclosureOutcome.Refused, None
                DisclosureKind.AttachmentList, DisclosureOutcome.Succeeded, None
                DisclosureKind.CaseList, DisclosureOutcome.Succeeded, None
                DisclosureKind.CaseList, DisclosureOutcome.Succeeded, None
            ]
            |> List.sort

        Expect.equal
            observed
            expected
            "Direct reads and lists each record an observation with minimized evidence"

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
                Limit = 1
            }

        let! first = Service.disclosureTrail owner filtered token
        let first = success first
        Expect.equal first.Items.Length 1 "Apply the requested page size"
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

        let! timelineOnly =
            Service.disclosureTrail
                owner
                { filtered with
                    Kind = Some DisclosureKind.CaseTimeline
                }
                token

        let timelineOnly = success timelineOnly
        Expect.hasLength timelineOnly.Items 1 "The direct timeline has one distinct observation"
        let timelineEvent = timelineOnly.Items.Head.Event
        Expect.equal timelineEvent.Kind DisclosureKind.CaseTimeline "Case detail is excluded"

        Expect.equal
            timelineEvent.Page
            (Some(DisclosurePageEvidence.create history.Items.Length [] []))
            "Only released history rows contribute to page evidence"

        Expect.isNone timelineOnly.NextCursor "Detail observations do not extend timeline traversal"

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
                Page = Some(DisclosurePageEvidence.create 1 [] [])
            }

        Expect.equal
            trailEvents
            [
                trailEvent
                trailEvent
                trailEvent
                { trailEvent with
                    Outcome = DisclosureOutcome.Refused
                    Page = None
                }
                { trailEvent with
                    Outcome = DisclosureOutcome.Refused
                    Page = None
                }
            ]
            "Released pages and the rejected cursor have separate safe evidence"

        // Opening another case makes a one-row traversal exercise the store's lookahead.
        let! following =
            Service.executeSubmission
                owner
                (request fixture 189 "LOOKAHEAD-PRIVATE" 0L "case.open" Map.empty)
                token

        accepted following |> ignore

        let! returned =
            Service.list
                owner
                {
                    Filter = CaseListFilter.empty
                    Page = { Limit = 1; AfterCursor = None }
                }
                token

        let page = success returned
        Expect.isSome page.NextCursor "One additional case is known only to lookahead"

        let! events = allEvents fixture
        let membership = events.Head.Event.Page |> Option.get
        Expect.equal membership.RowCount 1 "The evidence counts returned records"

        Expect.equal
            membership.Cases
            (page.Items |> List.map _.CaseId)
            "Lookahead contributes no retained subjects"

        Expect.isEmpty membership.People "A case list does not retain profiles in its evidence"

        let! pending = Service.operationGet owner { OperationId = id fixture 188 } token

        match success pending.Outcome with
        | OperationLookupResult.Pending _ -> ()
        | other -> failtestf "Expected a pending observation, received %A" other

        let! observed = allEvents fixture
        let latest = observed.Head.Event

        Expect.equal
            latest.Kind
            DisclosureKind.OperationLookup
            "An authorized empty observation is audited"

        Expect.equal latest.ActorId actorId "The observation is attributable"
        Expect.isNone latest.CaseId "An empty observation has no case subject"
        Expect.isNone latest.AttachmentId "An empty observation has no attachment subject"
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
        let! before = allEvents fixture
        let mutable attempts = 0

        let failing =
            failingAudit (fun () -> Interlocked.Increment(&attempts) |> ignore) owner

        let! dossier = Service.get failing { Target = target fixture } token
        expectStoreFailure dossier
        let! history = timeline failing (target fixture)
        expectStoreFailure history
        let! receipt = Service.operationGet failing { OperationId = id fixture 10 } token
        expectStoreFailure receipt.Outcome

        let! downloaded =
            Service.downloadAttachment failing (attachmentRequest fixture attachment) token

        expectStoreFailure downloaded

        let! previewed =
            Service.previewAttachment failing (attachmentRequest fixture attachment) token

        expectStoreFailure previewed
        let! trail = Service.disclosureTrail failing selection token
        expectStoreFailure trail
        Expect.equal attempts 6 "Each read attempts the separate audit write before its result"
        let! actual = allEvents fixture
        Expect.equal actual before "The injected audit failure wrote no rows"
    }

let private withdrawalBeforeAttachmentRelease (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = VerificationSupport.populated database
        let! attachment = upload fixture owner
        let mutable completedReads = 0

        let paused =
            owner
            |> Service.mapStore (fun store ->
                { new IStore with
                    member _.Context operation = store.Context operation
                    member _.Write operation = store.Write operation
                    member _.RecordDisclosure(event, ct) = store.RecordDisclosure(event, ct)

                    member _.Read operation =
                        task {
                            let! result = store.Read operation

                            if Interlocked.Increment(&completedReads) = 1 then
                                let withdrawal =
                                    Submissions.createRaw
                                        "attachment.withdraw"
                                        (id fixture 78)
                                        (OperationBinding.Attachment(
                                            target fixture,
                                            Some(AttachmentId.value attachment)
                                        ))
                                        Map.empty

                                let! withdrawn = Service.executeSubmission owner withdrawal token
                                accepted withdrawn |> ignore

                            return result
                        }
                })

        let! downloaded =
            Service.downloadAttachment paused (attachmentRequest fixture attachment) token

        expectUnavailable downloaded

        Expect.equal
            completedReads
            2
            "The second observation checks current withdrawal after preparation"

        let! events = allEvents fixture

        Expect.isFalse
            (events
             |> List.exists (fun row ->
                 row.Event.Kind = DisclosureKind.AttachmentDownload
                 && row.Event.Outcome = DisclosureOutcome.Succeeded))
            "The prepared bytes were never authorized for release"
    }

let private ordinaryCutBeforeAcknowledgement (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = VerificationSupport.populated database

        let note =
            Submissions.createRaw
                "note.add"
                (id fixture 80)
                (OperationBinding.Note(target fixture, None))
                (Map.ofList
                    [
                        Submissions.input InputTarget.NoteText,
                        SubmissionValue.Text "ordinary-cut-synthetic-note"
                    ])

        let! result = Service.executeSubmission owner note token
        accepted result |> ignore

        let recorded =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let acknowledge =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let paused =
            owner
            |> Service.mapStore (fun store ->
                { new IStore with
                    member _.Context operation = store.Context operation
                    member _.Write operation = store.Write operation
                    member _.Read operation = store.Read operation

                    member _.RecordDisclosure(event, ct) =
                        task {
                            let! committed = store.RecordDisclosure(event, ct)
                            recorded.TrySetResult() |> ignore
                            do! acknowledge.Task.WaitAsync(ct)
                            return committed
                        }
                })

        let read service =
            Service.notes
                service
                {
                    Target = target fixture
                    AfterCursor = None
                    Limit = 10
                }
                token

        let prior = read paused

        try
            do! recorded.Task.WaitAsync(TimeSpan.FromSeconds 15.)
            Expect.isFalse prior.IsCompleted "A committed observation alone does not release a body"

            for number, role in [ 81, Role.CaseHandler; 82, Role.PaymentRecorder ] do
                let! revoked =
                    grant
                        owner
                        (id fixture number)
                        fixture.Owner
                        {
                            Role = role
                            Scope = GrantTarget.Installation
                        }
                        false

                acceptedPeople revoked

            let! current = read owner
            expectUnavailable current
        finally
            acknowledge.TrySetResult() |> ignore

        let! prior = prior

        Expect.hasLength
            (success prior).Notes.Items
            1
            "The acknowledged read keeps its original cut"

        let! observed = allEvents fixture

        let notes =
            observed |> List.filter (fun row -> row.Event.Kind = DisclosureKind.CaseNotes)

        Expect.hasLength notes 2 "Direct note requests produce both observations"
        Expect.isNone notes.Head.Event.CaseId "The refused target is opaque"
        Expect.isSome notes.Tail.Head.Event.CaseId "The prior allowed cut records the case"
    }

type private Cut =
    | BeforeSnapshot
    | AwaitingAcknowledgement

let private atCut cut services (observe: ServiceContext -> Task<'reply>) change =
    task {
        let arrived =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let resume =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let paused =
            services
            |> Service.mapStore (fun store ->
                { new IStore with
                    member _.Context operation = store.Context operation
                    member _.Write operation = store.Write operation

                    member _.Read operation =
                        task {
                            if cut = BeforeSnapshot then
                                arrived.TrySetResult() |> ignore
                                do! resume.Task.WaitAsync(operation.CancellationToken)

                            return! store.Read operation
                        }

                    member _.RecordDisclosure(event, ct) =
                        task {
                            let! recorded = store.RecordDisclosure(event, ct)

                            if cut = AwaitingAcknowledgement then
                                arrived.TrySetResult() |> ignore
                                do! resume.Task.WaitAsync(ct)

                            return recorded
                        }
                })

        let observation = observe paused

        try
            do! arrived.Task.WaitAsync(TimeSpan.FromSeconds 15.)
            Expect.isFalse observation.IsCompleted "The response stays private at the barrier"
            do! change ()
        finally
            resume.TrySetResult() |> ignore

        return! observation
    }

let private acknowledgementReleasesPool () =
    async {
        let! database =
            PostgresFixture.createForPool ApplicationFixtures.owner 1 |> Async.AwaitTask

        try
            do!
                task {
                    let! owner = VerificationSupport.populated database

                    let observe service =
                        Service.get service { Target = target database.Fixture } token

                    let acquire () =
                        task {
                            use limit = new CancellationTokenSource(TimeSpan.FromSeconds 5.)

                            use! connection =
                                database.RuntimeSource.OpenConnectionAsync(limit.Token)

                            let! rows = DatabaseSession.rows connection "SELECT 1" [||] limit.Token

                            Expect.hasLength
                                rows
                                1
                                "The original snapshot and audit connections are both returned"
                        }

                    let! completed = atCut AwaitingAcknowledgement owner observe acquire
                    success completed |> ignore

                    let! after = observe owner
                    success after |> ignore
                }
                |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

let private erasureAtCut cut (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = VerificationSupport.populated database

        let! steward =
            grant
                owner
                (id fixture 87)
                fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople steward

        let query =
            {
                CaseGetRequest.Target = target fixture
            }

        let observe service = Service.get service query token

        let erase () =
            task {
                let command = request fixture 88 "synthetic-a" 1L "case.erase" Map.empty
                let! erased = Service.executeSubmission owner command token
                accepted erased |> ignore
            }

        let! original = observe owner
        let snapshot = (success original).Snapshot

        Expect.isSome
            (CaseState.fields snapshot.State)
            "The positive control contains private case facts"

        let! raced = atCut cut owner observe erase
        let raced = (success raced).Snapshot
        Expect.equal raced.CaseId snapshot.CaseId "Erasure retains the Case ID"

        Expect.equal
            raced.State
            (if cut = BeforeSnapshot then
                 CaseState.Erased
             else
                 snapshot.State)
            "Only an earlier authorized snapshot releases its original facts"

        let! current = observe owner

        Expect.equal
            (success current).Snapshot.State
            CaseState.Erased
            "A subsequent snapshot sees erasure"
    }

let private peopleAtCut cut disable (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = VerificationSupport.populated database

        let principal =
            PrincipalKey.human "https://issuer.example/" "synthetic-boundary-reader"
            |> Result.defaultWith (fun _ -> failtest "Expected a synthetic principal")

        let submit number principal taskName values =
            task {
                let! command =
                    personSubmission
                        fixture.Store
                        (id fixture number)
                        principal
                        None
                        taskName
                        values

                let! result = Service.executeSubmission owner command token
                acceptedPeople result
            }

        do!
            submit
                87
                principal
                "people.register"
                (Submissions.profile
                    {
                        DisplayName = "Synthetic snapshot profile"
                        Contact = Some "Synthetic snapshot contact"
                        DigestEmail = None
                    })

        let! grantRead =
            grant
                owner
                (id fixture 88)
                principal
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople grantRead

        if disable then
            let reader = core fixture principal

            let observe service =
                Service.get service { Target = target fixture } token

            let! normal = observe reader
            let initial = (success normal).Snapshot

            let! raced =
                atCut cut reader observe (fun () -> submit 89 principal "people.disable" Map.empty)

            if cut = BeforeSnapshot then
                expectUnavailable raced
            else
                Expect.equal
                    (success raced).Snapshot
                    initial
                    "Disablement after the cut does not rewrite the prior observation"

            let! current = observe reader
            expectUnavailable current
        else
            let! access =
                grant
                    owner
                    (id fixture 90)
                    fixture.Owner
                    {
                        Role = Role.DataSteward
                        Scope = GrantTarget.Installation
                    }
                    true

            acceptedPeople access

            let query =
                {
                    GrantsRequest.ActorId = id fixture 87
                    AfterCursor = None
                    Limit = 10
                }

            let observe service = Service.grants service query token
            let! control = observe owner
            let control = success control
            Expect.isSome control.Actor.Contact "The original contact is visible"

            let! raced =
                atCut cut owner observe (fun () ->
                    submit 91 principal "people.eraseProfile" Map.empty)

            let result = success raced

            Expect.equal
                result.Actor.Actor.ActorId
                control.Actor.Actor.ActorId
                "Profile erasure retains identity"

            Expect.equal
                result.Actor.Contact
                (if cut = BeforeSnapshot then None else control.Actor.Contact)
                "Contact follows the authorized snapshot"

            Expect.equal
                (result.Items |> List.map _.Role)
                [ Role.CaseReader ]
                "Authority remains reviewable"

            let! current = observe owner

            Expect.isNone
                (success current).Actor.Contact
                "The subsequent read withholds cleared contact"
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
                    let caseBody = Codec.encode Api.caseGet.Input { Target = target fixture }

                    let timelineBody =
                        Codec.encode
                            Api.caseTimeline.Input
                            {
                                Target = target fixture
                                Filter = None
                                AfterCursor = None
                                Limit = 10
                            }

                    let timelineFault =
                        Codec.encode
                            Api.caseTimeline.Output
                            (RepresentationReply.Data(Outcome.Failed CoreFault.SchemaMismatch))

                    let attachmentBody =
                        Codec.encode
                            AttachmentTransport.request
                            (attachmentRequest fixture attachment)

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
                            "case.timeline", timelineBody, "application/json", timelineFault
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
                        elif endpoint = "case.timeline" then
                            match
                                Codec.decode
                                    Limits.MaximumResponseBytes
                                    Api.caseTimeline.Output
                                    bytes
                            with
                            | Ok(RepresentationReply.Data(Outcome.Succeeded page)) ->
                                Expect.isNonEmpty
                                    page.Items
                                    "The direct timeline releases retained rows"
                            | _ -> failtest "The timeline must succeed before fault injection."
                        else
                            Expect.sequenceEqual
                                bytes
                                (Encoding.ASCII.GetBytes "%PDF-1.7\n")
                                endpoint

                    let! before = allEvents fixture

                    Expect.equal
                        (before
                         |> List.filter (fun row ->
                             row.Event.Kind <> DisclosureKind.OperationReceipt)
                         |> List.length)
                        4
                        "Each positive HTTP read records a disclosure"

                    use! human =
                        host.CallResponse(
                            "case.get",
                            caseBody,
                            credential,
                            representation = Representation.Human
                        )

                    let! humanBytes = human.Content.ReadAsByteArrayAsync()

                    match
                        Codec.decode Limits.MaximumResponseBytes Api.caseGet.Output humanBytes
                    with
                    | Ok(RepresentationReply.Human {
                                                       Outcome = ClaimCore.Presentation.HumanReadOutcome.Succeeded _
                                                   }) -> ()
                    | _ ->
                        failtest
                            "The same authorized case releases through the HUMAN representation"

                    let! afterHuman = allEvents fixture

                    let detailEvidence (rows: RecordedDisclosure list) =
                        rows
                        |> List.filter (fun row -> row.Event.Kind = DisclosureKind.CaseRead)
                        |> List.map _.Event

                    let dataAudit = detailEvidence before |> List.exactlyOne

                    Expect.equal
                        (detailEvidence afterHuman)
                        [ dataAudit; dataAudit ]
                        "HUMAN and DATA share the same detail evidence"

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
                    Expect.equal after afterHuman "The failed PostgreSQL inserts added no events"

                    for cut in [ BeforeSnapshot; AwaitingAcknowledgement ] do
                        use aborted = new CancellationTokenSource()

                        let read paused =
                            task {
                                use interrupted =
                                    new TestServerFixture.Host(
                                        true,
                                        installationId = fixture.InstallationId,
                                        forActor = (fun _ -> paused)
                                    )

                                let! _ = interrupted.Login()
                                let! credential = interrupted.Token()

                                try
                                    use! response =
                                        interrupted.CallResponse(
                                            "case.get",
                                            caseBody,
                                            credential,
                                            cancellationToken = aborted.Token
                                        )

                                    failtestf
                                        "An aborted request delivered status %A"
                                        response.StatusCode
                                with :? OperationCanceledException ->
                                    ()
                            }

                        let! beforeAbort = allEvents fixture

                        do!
                            atCut cut owner read (fun () ->
                                aborted.Cancel()
                                Task.FromResult())

                        let! afterAbort = allEvents fixture
                        let expected = if cut = BeforeSnapshot then 0 else 1

                        Expect.equal
                            ((detailEvidence afterAbort).Length
                             - (detailEvidence beforeAbort).Length)
                            expected
                            "An aborted response retains only evidence already committed at its cut"

                    use! recovered = call "case.get" caseBody

                    Expect.equal
                        (int recovered.StatusCode)
                        200
                        "New requests work after either abort"
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
        let! before = allEvents fixture

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
        let audited = audited |> List.filter (fun row -> not (List.contains row before))
        Expect.hasLength audited 4 "Each replay or conflict page adds one row"

        Expect.equal
            (audited
             |> List.filter (fun row -> row.Event.Kind = DisclosureKind.OperationReceipt)
             |> List.length)
            1
            "An executed replay discloses only after the fresh receipt observation"

        for entry in audited do
            Expect.equal entry.Event.CaseId (Some caseId) "Record the retained case identity"

            Expect.equal
                entry.Event.Outcome
                DisclosureOutcome.Succeeded
                "Attests to released history"

        let failing = failingAudit ignore owner

        let newNote =
            Submissions.createRaw
                "note.add"
                (id fixture 76)
                (OperationBinding.Note(target fixture, None))
                (Map.ofList
                    [
                        Submissions.input InputTarget.NoteText,
                        SubmissionValue.Text "synthetic receipt failure"
                    ])

        let! first = Service.executeSubmission failing newNote token

        Expect.equal
            first.Outcome
            ExecuteResult.ReceiptUnavailable
            "A known write can settle before receipt auditing fails"

        let! recovered = Service.operationGet owner { OperationId = id fixture 76 } token

        match success recovered.Outcome with
        | OperationLookupResult.Accepted(_, true) -> ()
        | _ -> failtest "The original identity recovers the settled operation"

        let! replayed = Service.executeSubmission owner newNote token
        replayReceipt replayed |> ignore


        for operation, alreadyAccepted in [ original, true; stale, false ] do
            let! preview = Service.previewSubmission failing operation token
            expectStoreFailure preview.Outcome
            let! execution = Service.executeSubmission failing operation token

            match execution.Outcome, alreadyAccepted with
            | ExecuteResult.ReceiptUnavailable, true
            | ExecuteResult.Failed CoreFault.StoreUnavailable, false -> ()
            | _ ->
                failtest "Withhold the unaudited reply and preserve the original recovery identity."
    }

[<Tests>]
let tests =
    testList
        "Disclosure read behavior"
        [
            testCaseAsync
                "ordinary acknowledgement returns the single main pool slot"
                (acknowledgementReleasesPool ())
            for cut in [ BeforeSnapshot; AwaitingAcknowledgement ] do
                testCaseAsync
                    ($"ordinary case erasure at {cut}")
                    (ApplicationFixtures.run (fun database _ -> erasureAtCut cut database))

                testCaseAsync
                    ($"ordinary account disablement at {cut}")
                    (ApplicationFixtures.run (fun database _ -> peopleAtCut cut true database))

                testCaseAsync
                    ($"ordinary profile erasure at {cut}")
                    (ApplicationFixtures.run (fun database _ -> peopleAtCut cut false database))
            testCaseAsync
                "case and attachment disclosures are exact and the trail is Auditor-only"
                (ApplicationFixtures.run (fun database _ -> disclosureReads database))
            testCaseAsync
                "an audit write failure blocks every case and attachment result"
                (ApplicationFixtures.run (fun database _ -> failClosed database))
            testCaseAsync
                "PostgreSQL disclosure insert failure withholds case, timeline, download and preview HTTP content"
                (postgresFailureAtHttpBoundary ())
            testCaseAsync
                "attachment withdrawal before the release cut withholds prepared bytes"
                (ApplicationFixtures.run (fun database _ ->
                    withdrawalBeforeAttachmentRelease database))
            testCaseAsync
                "ordinary note reads wait for acknowledgement and keep their original authorization cut"
                (ApplicationFixtures.run (fun database _ ->
                    ordinaryCutBeforeAcknowledgement database))
            testCaseAsync
                "receipt replays and revision conflicts audit retained disclosures"
                (ApplicationFixtures.run (fun database _ -> retainedReplies database))
        ]
