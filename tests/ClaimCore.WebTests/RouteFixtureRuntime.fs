module ClaimCore.WebTests.RouteFixtureRuntime

open System
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Web
open ClaimCore.WebTests.RouteFixtureDescription
open ClaimCore.WebTests.RouteFixtureRecoveryFallback

let operationId = Guid.Parse("40000000-0000-4000-8000-000000000001")

let private stubResult configured fallback =
    Task.FromResult(defaultArg configured fallback)

let private absentResolve =
    ResolveOutcome.RefusedBeforeAttempt(None, RecoveryRejection.PreparationNotFound)

type RuntimeStub(?invalidExportMetadata: bool) as this =
    let pendingRecoveryPage =
        RecoveryQueryOutcome.RecoverySucceeded
            {
                View = RecoveryListView.Pending
                Items = []
                NextCursor = None
                PendingPreparationCount = 0
                PendingCanonicalRequestBytes = 0L
                MaximumPendingPreparations = 1024
                MaximumPendingCanonicalRequestBytes = 64L * 1024L * 1024L
                NearCapacity = false
            }

    let recoveryList cursor =
        this.RecoveryCalls <- this.RecoveryCalls + 1
        this.LastRecoveryCursor <- Some cursor
        stubResult this.RecoveryListOutcome pendingRecoveryPage

    let recoveryInspect () =
        this.RecoveryCalls <- this.RecoveryCalls + 1

        stubResult
            this.RecoveryInspectOutcome
            (RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound operationId))

    let recoveryResolve () =
        this.RecoveryCalls <- this.RecoveryCalls + 1
        stubResult this.ResolveOutcome absentResolve

    let recoveryDismiss () =
        this.RecoveryCalls <- this.RecoveryCalls + 1
        stubResult this.DismissOutcome (RecoveryDismissOutcome.DismissNotFound operationId)

    let recoveryExport () =
        this.RecoveryCalls <- this.RecoveryCalls + 1

        stubResult
            this.ExportOutcome
            (exportFallback operationId (defaultArg invalidExportMetadata false))

    let envelopePreview () =
        this.RecoveryCalls <- this.RecoveryCalls + 1
        stubResult this.EnvelopePreviewOutcome RecoveryQueryOutcome.RecoveryCancelled

    let envelopeRetain () =
        this.RecoveryCalls <- this.RecoveryCalls + 1

        stubResult
            this.EnvelopeRetainOutcome
            RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission

    let recovery =
        { new IRecoveryWorkflow with
            member _.List(_, cursor, _, _) = recoveryList cursor
            member _.Inspect(_, _, _, _) = recoveryInspect ()
            member _.Resolve(_, _, _) = recoveryResolve ()
            member _.Dismiss(_, _, _, _) = recoveryDismiss ()
            member _.ExportEnvelope(_, _, _) = recoveryExport ()
            member _.PreviewEnvelopeImport(_, _) = envelopePreview ()
            member _.RetainEnvelopeImport(_, _, _) = envelopeRetain ()
        }

    let management =
        { new IActorManagement with
            member _.RegisterActor(_, _, _) =
                this.ManagementCalls <- this.ManagementCalls + 1

                Task.FromResult(
                    defaultArg this.ManagementOutcome ActorManagementOutcome.ResourceUnavailable
                )

            member _.SetGrant(_, _, _, _, _, _) =
                this.ManagementCalls <- this.ManagementCalls + 1

                Task.FromResult(
                    defaultArg this.ManagementOutcome ActorManagementOutcome.ResourceUnavailable
                )

            member _.SetEnabled(_, _, _, _) =
                this.ManagementCalls <- this.ManagementCalls + 1

                Task.FromResult(
                    defaultArg this.ManagementOutcome ActorManagementOutcome.ResourceUnavailable
                )

            member _.Observe(_, _) =
                this.ManagementCalls <- this.ManagementCalls + 1

                Task.FromResult(
                    defaultArg this.ManagementOutcome ActorManagementOutcome.ResourceUnavailable
                )
        }

    let core =
        { new IClaimsCore with
            member _.Describe() = description

            member _.Prepare(draft, _) =
                this.CoreCalls <- this.CoreCalls + 1

                Task.FromResult(
                    defaultArg
                        this.PrepareOutcome
                        (PrepareOutcome.CancelledBeforeAdmission draft.OperationId)
                )

            member _.Execute(draft, _) =
                this.CoreCalls <- this.CoreCalls + 1

                Task.FromResult(
                    defaultArg
                        this.ExecuteOutcome
                        (SubmissionOutcome.CancelledBeforeAdmission draft.OperationId)
                )

            member _.Get(reference, _) =
                this.CoreCalls <- this.CoreCalls + 1

                Task.FromResult(
                    defaultArg this.GetOutcome (QueryOutcome.Succeeded(Lookup.NotFound reference))
                )

            member _.List(_, _) =
                this.CoreCalls <- this.CoreCalls + 1

                Task.FromResult(
                    defaultArg
                        this.ListOutcome
                        (QueryOutcome.Succeeded { Items = []; NextCursor = None })
                )

            member _.History(request, _) =
                this.CoreCalls <- this.CoreCalls + 1

                Task.FromResult(
                    defaultArg
                        this.HistoryOutcome
                        (QueryOutcome.Succeeded(Lookup.NotFound request.CaseReference))
                )

            member _.ObserveOperation(id, _) =
                this.CoreCalls <- this.CoreCalls + 1

                Task.FromResult(
                    defaultArg this.ObserveOutcome (QueryOutcome.Succeeded(Lookup.NotFound id))
                )

            member _.Recovery = recovery
        }

    let lifecycle =
        { new ICaseLifecycleWorkflow with
            member _.Review(_, _) =
                Task.FromResult LifecycleReviewOutcome.ResourceUnavailable

            member _.Apply(_, _) =
                Task.FromResult LifecycleWriteOutcome.ResourceUnavailable

            member _.Approve(_, _, _, _) =
                Task.FromResult LifecycleWriteOutcome.ResourceUnavailable
        }

    let tombstones =
        { new ITombstoneWorkflow with
            member _.Review(_, _) =
                this.TombstoneCalls <- this.TombstoneCalls + 1

                Task.FromResult(
                    defaultArg
                        this.TombstoneReviewOutcome
                        TombstoneReviewOutcome.ResourceUnavailable
                )

            member _.ApproveWitnessPrune(_, _, _, _) =
                this.TombstoneCalls <- this.TombstoneCalls + 1

                Task.FromResult(
                    defaultArg this.TombstoneWriteOutcome TombstoneWriteOutcome.ResourceUnavailable
                )

            member _.ApproveTerminal(_, _, _, _) =
                this.TombstoneCalls <- this.TombstoneCalls + 1

                Task.FromResult(
                    defaultArg this.TombstoneWriteOutcome TombstoneWriteOutcome.ResourceUnavailable
                )

            member _.ChangeHold(_, _) =
                this.TombstoneCalls <- this.TombstoneCalls + 1

                Task.FromResult(
                    defaultArg this.TombstoneWriteOutcome TombstoneWriteOutcome.ResourceUnavailable
                )
        }

    let actorCore =
        { new IActorClaimsCore with
            member _.Definition(_) =
                Task.FromResult(QueryOutcome.Succeeded description)

            member _.Prepare(request, token) = core.Prepare(request, token)
            member _.Execute(request, token) = core.Execute(request, token)
            member _.Get(reference, token) = core.Get(reference, token)
            member _.List(request, token) = core.List(request, token)
            member _.History(request, token) = core.History(request, token)

            member _.ObserveOperation(operationId, token) =
                core.ObserveOperation(operationId, token)

            member _.Recovery = recovery
            member _.Management = management
            member _.Lifecycle = lifecycle
            member _.Tombstones = tombstones

            member _.ApproveCopySigner(request, _) =
                Task.FromResult(CopySignerApprovalOutcome.StartedUnconfirmed request.ApprovalId)

            member _.ApproveCopyDeletion(request, _) =
                Task.FromResult(CopyDeletionApprovalOutcome.StartedUnconfirmed request.ApprovalId)

            member _.ApproveCopyAdoption(request, _) =
                Task.FromResult(CopyAdoptionApprovalOutcome.StartedUnconfirmed request.ApprovalId)

            member _.ApproveWriterHandoff(request, _) =
                Task.FromResult(WriterHandoffApprovalOutcome.StartedUnconfirmed request.ApprovalId)

            member _.ReviewRealDataActivation(_, _) =
                Task.FromResult RealDataActivationPlanReviewOutcome.ResourceUnavailable

            member _.ApproveRealDataActivation(request, _) =
                Task.FromResult(
                    RealDataActivationApprovalOutcome.StartedUnconfirmed request.ApprovalId
                )
        }

    member val CoreCalls = 0 with get, set
    member val RecoveryCalls = 0 with get, set
    member val ManagementCalls = 0 with get, set
    member val TombstoneCalls = 0 with get, set
    member val TombstoneReviewOutcome: TombstoneReviewOutcome option = None with get, set
    member val TombstoneWriteOutcome: TombstoneWriteOutcome option = None with get, set
    member val ManagementOutcome: ActorManagementOutcome option = None with get, set
    member val LastRecoveryCursor: string option option = None with get, set
    member val GetOutcome: QueryOutcome<Lookup<CurrentCase, string>> option = None with get, set
    member val ListOutcome: QueryOutcome<CaseSummaryPage> option = None with get, set

    member val HistoryOutcome: QueryOutcome<Lookup<HistoryResultPage, string>> option =
        None with get, set

    member val ObserveOutcome: QueryOutcome<Lookup<OperationReceipt, Guid>> option =
        None with get, set

    member val PrepareOutcome: PrepareOutcome option = None with get, set
    member val ExecuteOutcome: SubmissionOutcome option = None with get, set
    member val RecoveryListOutcome: RecoveryQueryOutcome<RecoveryPage> option = None with get, set

    member val RecoveryInspectOutcome: RecoveryQueryOutcome<Lookup<RecoveryInspection, Guid>> option =
        None with get, set

    member val ResolveOutcome: ResolveOutcome option = None with get, set
    member val DismissOutcome: RecoveryDismissOutcome option = None with get, set

    member val ExportOutcome: RecoveryQueryOutcome<Lookup<RecoveryExport, Guid>> option =
        None with get, set

    member val EnvelopePreviewOutcome: RecoveryQueryOutcome<RecoveryImportPreview> option =
        None with get, set

    member val EnvelopeRetainOutcome: RecoveryImportRetainOutcome option = None with get, set


    member _.Core = core
    member _.ActorCore = actorCore
