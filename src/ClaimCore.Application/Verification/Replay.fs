namespace ClaimCore.Application.Verification

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Application.Execution
open ClaimCore.Application.Storage
open ClaimCore.RecordFormat
open ClaimCore.Application.Verification.VerificationChecks

/// Verification adds checkpoint/finding context to the single application fold.
module internal Replay =
    type AttachmentOrigin =
        {
            Position: EffectPosition
            IntroducesBlob: bool
            Descriptor: AttachmentDescriptor option
        }

    type AttachmentFamily =
        {
            Origins: Map<AttachmentId, AttachmentOrigin>
            Current: Set<AttachmentId>
            ActiveBlobs: Set<AttachmentId>
            RetainedBlobCount: int64
            KnownRetainedBytes: int64
        }

    let private emptyAttachments =
        {
            Origins = Map.empty
            Current = Set.empty
            ActiveBlobs = Set.empty
            RetainedBlobCount = 0L
            KnownRetainedBytes = 0L
        }

    type Scan =
        {
            Fold: FoldState
            Attachments: Map<CaseId, AttachmentFamily>
            ExpectedSeen: bool
        }

        member this.Head = this.Fold.Head

    let private finding =
        function
        | FoldProblem.Link -> VerificationFinding.Link
        | FoldProblem.Identity
        | FoldProblem.Task -> VerificationFinding.PayloadFormat
        | FoldProblem.Executor ExecutorIssue.ActorNotEnabled -> VerificationFinding.ActorNotEnabled
        | FoldProblem.Executor ExecutorIssue.HumanRequired -> VerificationFinding.HumanRequired
        | FoldProblem.Executor ExecutorIssue.OwnerRequired -> VerificationFinding.OwnerRequired
        | FoldProblem.Decision -> VerificationFinding.StateSequence
        | FoldProblem.State RecordViolation.InvalidRevisionSequence ->
            VerificationFinding.RevisionSequence
        | FoldProblem.State RecordViolation.InvalidHistoryStart -> VerificationFinding.HistoryStart
        | FoldProblem.State _
        | FoldProblem.NaturalKey -> VerificationFinding.StateSequence
        | FoldProblem.Nonce -> VerificationFinding.Commitment
        | FoldProblem.Erasure -> VerificationFinding.PayloadWithoutErasure
        | FoldProblem.ErasureIncomplete -> VerificationFinding.ErasureIncomplete
        | FoldProblem.TargetHumanRequired -> VerificationFinding.HumanRequired

    let initial (expected: LedgerPosition option) =
        {
            Fold = Fold.initial
            Attachments = Map.empty
            ExpectedSeen = expected |> Option.exists ((=) LedgerPosition.genesis)
        }

    let link (state: Scan) sequence previous =
        if Sequence.value state.Head.Sequence = Sequence.Maximum then
            Error(divergence (Some sequence) VerificationFinding.Link)
        else
            let expected = Sequence.successor state.Head.Sequence

            if sequence > expected then
                Error(divergence (Some expected) VerificationFinding.MissingRoot)
            else
                require
                    (Some sequence)
                    VerificationFinding.Link
                    (sequence = expected && previous = EntryHash.toBytes state.Head.EntryHash)

    let private attachmentStep (families: Map<CaseId, AttachmentFamily>) (change: PartChange) =
        let invalid =
            Error(
                divergence
                    (Some(EffectPosition.sequence change.Stamp.Position))
                    VerificationFinding.AttachmentReferences
            )

        match change.Aggregate, change.Value, change.Erases with
        | AggregateReference.Family caseId, PartValue.Attachment effect, None ->
            let current = Map.tryFind caseId families |> Option.defaultValue emptyAttachments
            let id = change.PartId |> AttachmentId.create

            let update id family = Ok(Map.add id family families)

            match id, effect with
            | Some id, AttachmentEffect.Added(introduces, descriptor) when
                change.Revision = Revision.first
                && id = AttachmentId.ofOperationId change.Stamp.OperationId
                && not (Map.containsKey id current.Origins)
                ->
                let blobValid =
                    match introduces, descriptor with
                    | true, Some descriptor -> descriptor.Blob = id
                    | false, Some descriptor ->
                        Map.tryFind descriptor.Blob current.Origins
                        |> Option.exists (fun origin ->
                            origin.IntroducesBlob
                            && (origin.Descriptor
                                |> Option.exists (fun stored ->
                                    AttachmentIntent.length stored.Intent =
                                        AttachmentIntent.length descriptor.Intent
                                    && AttachmentIntent.digest stored.Intent =
                                        AttachmentIntent.digest descriptor.Intent)))
                    | true, None -> true
                    | false, None ->
                        current.Origins |> Map.exists (fun _ origin -> origin.IntroducesBlob)

                let activeBlob = descriptor |> Option.map _.Blob
                let length = descriptor |> Option.map (_.Intent >> AttachmentIntent.length)

                let capacity =
                    not introduces
                    || AttachmentAdmission.canRetainNew
                        current.RetainedBlobCount
                        current.KnownRetainedBytes
                        length

                if
                    not blobValid
                    || not capacity
                    || (activeBlob
                        |> Option.exists (fun blob -> Set.contains blob current.ActiveBlobs))
                then
                    invalid
                else
                    update
                        caseId
                        {
                            Origins =
                                Map.add
                                    id
                                    {
                                        Position = change.Stamp.Position
                                        IntroducesBlob = introduces
                                        Descriptor = descriptor
                                    }
                                    current.Origins
                            Current = Set.add id current.Current
                            RetainedBlobCount =
                                if introduces then
                                    current.RetainedBlobCount + 1L
                                else
                                    current.RetainedBlobCount
                            KnownRetainedBytes =
                                if introduces then
                                    current.KnownRetainedBytes + Option.defaultValue 0L length
                                else
                                    current.KnownRetainedBytes
                            ActiveBlobs =
                                activeBlob
                                |> Option.map (fun blob -> Set.add blob current.ActiveBlobs)
                                |> Option.defaultValue current.ActiveBlobs
                        }
            | Some id, AttachmentEffect.Withdrawn when Set.contains id current.Current ->
                let origin = Map.find id current.Origins

                update
                    caseId
                    { current with
                        Current = Set.remove id current.Current
                        ActiveBlobs =
                            origin.Descriptor
                            |> Option.map (fun descriptor ->
                                Set.remove descriptor.Blob current.ActiveBlobs)
                            |> Option.defaultValue current.ActiveBlobs
                    }
            | _ -> invalid
        | _, PartValue.Attachment _, _ -> invalid
        | _, _, Some(CompartmentOwner.Family id) ->
            Ok(
                Map.change
                    id
                    (Option.map (fun family ->
                        { family with
                            Current = Set.empty
                            ActiveBlobs = Set.empty
                        }))
                    families
            )
        | _ -> Ok families

    let append installation policy expected state record =
        Fold.append installation policy state.Fold record
        |> Result.mapError (fun problem ->
            divergence (Some record.Root.Header.Sequence) (finding problem))
        |> Result.bind (fun next ->
            record.Changes
            |> List.fold
                (fun state change ->
                    state |> Result.bind (fun families -> attachmentStep families change))
                (Ok state.Attachments)
            |> Result.map (fun attachments ->
                {
                    Fold = next
                    Attachments = attachments
                    ExpectedSeen =
                        state.ExpectedSeen || (expected |> Option.exists ((=) next.Head))
                }))

    let private attachmentOrigins state =
        state.Attachments
        |> Map.toSeq
        |> Seq.collect (fun (caseId, family) ->
            family.Origins
            |> Map.toSeq
            |> Seq.map (fun (id, origin) -> (caseId, id), origin))

    let attachmentReferences state =
        attachmentOrigins state
        |> Seq.map (fun (key, origin) ->
            key, (origin.Position, origin.Descriptor |> Option.map _.Blob))
        |> Map.ofSeq

    let attachmentBlobs state =
        attachmentOrigins state
        |> Seq.choose (fun (key, origin) ->
            if origin.IntroducesBlob then
                Some(key, (origin.Position, origin.Descriptor))
            else
                None)
        |> Map.ofSeq

    let attachmentDescriptors state =
        attachmentOrigins state
        |> Seq.choose (fun ((caseId, _), origin) ->
            origin.Descriptor
            |> Option.map (fun descriptor -> (caseId, descriptor.Blob), descriptor))
        |> Seq.groupBy fst
        |> Seq.map (fun (blob, entries) -> blob, (entries |> Seq.map snd |> Seq.toList))
        |> Map.ofSeq

    let attachmentProjection state =
        state.Attachments
        |> Map.toSeq
        |> Seq.collect (fun (caseId, family) -> family.Current |> Seq.map (fun id -> caseId, id))
        |> Set.ofSeq

    let erasure state =
        Fold.complete state.Fold
        |> Result.map ignore
        |> Result.mapError (fun problem -> divergence None (finding problem))

    let head (state: Scan) actual =
        require None VerificationFinding.Head (actual = Some state.Head)

    let complete (expected: LedgerPosition option) (state: Scan) =
        if expected.IsSome && not state.ExpectedSeen then
            Error(
                divergence (expected |> Option.map _.Sequence) VerificationFinding.ExpectedPosition
            )
        else
            Ok state.Head

    let familyRows state =
        state.Fold.Families
        |> Map.toList
        |> List.fold
            (fun result (id, family) ->
                result
                |> Result.bind (fun rows ->
                    let parts =
                        Map.tryFind (CompartmentOwner.Family id) state.Fold.Parts
                        |> Option.map (Map.values >> Seq.toList)
                        |> Option.defaultValue []

                    ProjectionRows.family
                        family
                        parts
                        state.Fold.Origins
                        (Map.tryFind id state.Fold.Summaries)
                        state.Fold.ReportDates[id]
                    |> Result.mapError (fun _ ->
                        divergence None VerificationFinding.CaseProjection)
                    |> Result.map (fun row -> Map.add id row rows)))
            (Ok Map.empty)

    let peopleRows state =
        state.Fold.People
        |> Map.toList
        |> List.fold
            (fun result (id, person) ->
                result
                |> Result.bind (fun rows ->
                    ProjectionRows.person person
                    |> Result.mapError (fun _ ->
                        divergence None VerificationFinding.PeopleProjection)
                    |> Result.map (fun row -> Map.add id row rows)))
            (Ok Map.empty)

    let caseRecords (families: Result<Map<CaseId, FamilyProjectionDelta>, VerificationDivergence>) =
        families
        |> Result.map (
            Map.map (fun id row ->
                {
                    StoredCase.CaseId = id
                    Snapshot = row.Snapshot
                    SnapshotBytes = CaseRecord.encodeSnapshot row.Snapshot
                    Index = row.Index
                    ReportDates = row.ReportDates
                })
        )

    let workRows (families: Result<Map<CaseId, FamilyProjectionDelta>, VerificationDivergence>) =
        families
        |> Result.map (
            Map.map (fun id row ->
                let work = row.Work

                {
                    StoredWork.CaseId = id
                    Revision = work.Revision
                    Snapshot = work.Snapshot
                    SnapshotBytes =
                        work.Snapshot
                        |> Option.map WorkRecord.encodeSnapshot
                        |> Option.defaultValue [||]
                    ChangedAt = work.Changed |> Option.map _.RecordedAt
                    RecordedBy = work.Changed |> Option.map _.RecordedBy
                    SummaryChangedAt = work.SummaryChanged |> Option.map _.RecordedAt
                    SummaryRecordedBy = work.SummaryChanged |> Option.map _.RecordedBy
                })
        )

    let reminderRows
        (families: Result<Map<CaseId, FamilyProjectionDelta>, VerificationDivergence>)
        =
        families
        |> Result.map (fun rows ->
            rows
            |> Map.toList
            |> List.collect (fun (caseId, row) ->
                row.Reminders
                |> List.map (fun reminder ->
                    let snapshot =
                        Reminder.ofValue reminder.Id caseId reminder.Revision reminder.Value

                    reminder.Id,
                    {
                        StoredReminder.ReminderId = reminder.Id
                        CaseId = caseId
                        Revision = reminder.Revision
                        Snapshot = snapshot
                        SnapshotBytes =
                            snapshot
                            |> Option.map ReminderRecord.encodeSnapshot
                            |> Option.defaultValue [||]
                        RecordedAt = reminder.Changed.RecordedAt
                        RecordedBy = reminder.Changed.RecordedBy
                    }))
            |> Map.ofList)

    let noteRows (families: Result<Map<CaseId, FamilyProjectionDelta>, VerificationDivergence>) =
        families
        |> Result.map (fun rows ->
            rows
            |> Map.toList
            |> List.collect (fun (caseId, row) ->
                row.Notes
                |> List.map (fun note ->
                    note.Id,
                    {
                        StoredNote.Note = Note.ofAccepted note.Id caseId note.Value.Text
                        CreatedPosition = note.Origin.Position
                        RecordedAt = note.Origin.RecordedAt
                        RecordedBy = ProjectionRows.familyActor note.Origin.Authority
                    }))
            |> Map.ofList)

    let partyRows (families: Result<Map<CaseId, FamilyProjectionDelta>, VerificationDivergence>) =
        families
        |> Result.map (fun rows ->
            rows
            |> Map.toList
            |> List.collect (fun (caseId, row) ->
                row.Parties
                |> List.map (fun party ->
                    party.Id,
                    {
                        StoredParty.PartyId = party.Id
                        CaseId = caseId
                        Revision = party.Revision
                        CreatedPosition = party.Origin.Position
                        Value = party.Value
                    }))
            |> Map.ofList)

    let payments (families: Result<Map<CaseId, FamilyProjectionDelta>, VerificationDivergence>) =
        families
        |> Result.map (fun rows ->
            rows
            |> Map.toList
            |> List.collect (fun (caseId, row) ->
                row.Payments
                |> List.map (fun payment ->
                    let content =
                        match payment.Value.Entry with
                        | Some entry ->
                            PaymentProjectionContent.Available(entry, payment.Value.Removed)
                        | None -> PaymentProjectionContent.Cleared payment.Value.Removed

                    payment.Id,
                    {
                        PaymentProjectionRow.CaseId = caseId
                        PaymentId = payment.Id
                        Position = payment.Origin.Position
                        Content = content
                    }))
            |> Map.ofList)
