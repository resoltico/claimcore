module ClaimCore.WebTests.RouteFixtures

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Application
open ClaimCore.Web

let operationId = Guid.Parse("40000000-0000-4000-8000-000000000001")

let private description =
    {
        Contract = SemanticContract.current
        SemanticFingerprint = SemanticContract.fingerprint SemanticContract.current
        Runtime =
            {
                ProductVersion = "0.1.0"
                EffectiveBusinessDate = DateOnly(2026, 9, 9)
                TimeZoneId = "Etc/UTC"
            }
    }

let private stubResult configured fallback =
    Task.FromResult(defaultArg configured fallback)

let private absentResolve =
    ResolveOutcome.RefusedBeforeAttempt(
        None,
        {
            Code = RecoveryRejectionCode.PreparationNotFound
            Message = "Synthetic preparation absence."
            Action = RecommendedAction.ReadCurrent
        }
    )

let private exportFallback invalidMetadata =
    if invalidMetadata then
        RecoveryQueryOutcome.RecoverySucceeded(
            Lookup.Found
                {
                    Bytes = [||]
                    FileName = "synthetic-invalid-name.json"
                    MediaType = "application/vnd.claimcore.recovery+json"
                    RequestSha256 = String.replicate 64 "a"
                }
        )
    else
        RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound operationId)

type RuntimeStub(?invalidExportMetadata: bool) as this =
    let recovery =
        { new IRecoveryWorkflow with
            member _.List(cursor, _, _) =
                this.RecoveryCalls <- this.RecoveryCalls + 1
                this.LastRecoveryCursor <- Some cursor

                stubResult
                    this.RecoveryListOutcome
                    (RecoveryQueryOutcome.RecoverySucceeded { Items = []; NextCursor = None })

            member _.Inspect(_, _) =
                this.RecoveryCalls <- this.RecoveryCalls + 1

                stubResult
                    this.RecoveryInspectOutcome
                    (RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound operationId))

            member _.Resolve(_, _, _) =
                this.RecoveryCalls <- this.RecoveryCalls + 1
                stubResult this.ResolveOutcome absentResolve

            member _.Dismiss(_, _, _, _) =
                this.RecoveryCalls <- this.RecoveryCalls + 1
                stubResult this.DismissOutcome (RecoveryDismissOutcome.DismissNotFound operationId)

            member _.ExportEnvelope(_, _, _) =
                this.RecoveryCalls <- this.RecoveryCalls + 1

                stubResult
                    this.ExportOutcome
                    (exportFallback (defaultArg invalidExportMetadata false))

            member _.PreviewEnvelopeImport(_, _) =
                this.RecoveryCalls <- this.RecoveryCalls + 1
                stubResult this.EnvelopePreviewOutcome RecoveryQueryOutcome.RecoveryCancelled

            member _.RetainEnvelopeImport(_, _, _) =
                this.RecoveryCalls <- this.RecoveryCalls + 1

                stubResult
                    this.EnvelopeRetainOutcome
                    RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission

            member _.PreviewCanonicalRecordImport(_, _) =
                this.RecoveryCalls <- this.RecoveryCalls + 1
                stubResult this.RecordPreviewOutcome RecoveryQueryOutcome.RecoveryCancelled

            member _.RetainCanonicalRecordImport(_, _, _) =
                this.RecoveryCalls <- this.RecoveryCalls + 1

                stubResult
                    this.RecordRetainOutcome
                    RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission
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
                        (QueryOutcome.Succeeded
                            {
                                Items = []
                                NextAfterReference = None
                            })
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

    member val CoreCalls = 0 with get, set
    member val RecoveryCalls = 0 with get, set
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

    member val RecoveryInspectOutcome: RecoveryQueryOutcome<Lookup<RecoveryDetails, Guid>> option =
        None with get, set

    member val ResolveOutcome: ResolveOutcome option = None with get, set
    member val DismissOutcome: RecoveryDismissOutcome option = None with get, set

    member val ExportOutcome: RecoveryQueryOutcome<Lookup<RecoveryExport, Guid>> option =
        None with get, set

    member val EnvelopePreviewOutcome: RecoveryQueryOutcome<RecoveryImportPreview> option =
        None with get, set

    member val EnvelopeRetainOutcome: RecoveryImportRetainOutcome option = None with get, set

    member val RecordPreviewOutcome: RecoveryQueryOutcome<RecoveryImportPreview> option =
        None with get, set

    member val RecordRetainOutcome: RecoveryImportRetainOutcome option = None with get, set

    member _.Core = core

let context content =
    let value = DefaultHttpContext()
    value.Request.Body <- new MemoryStream(Encoding.UTF8.GetBytes(content: string))
    value.Response.Body <- new MemoryStream()
    value

let admit _ = Task.FromResult(Ok())

let execute (context: HttpContext) (result: IResult) =
    let services = ServiceCollection()
    services.AddOptions() |> ignore
    services.AddLogging() |> ignore
    context.RequestServices <- services.BuildServiceProvider()
    result.ExecuteAsync(context).GetAwaiter().GetResult()
    Encoding.UTF8.GetString((context.Response.Body :?> MemoryStream).ToArray())

let readResult (operation: Task<IResult>) = operation.GetAwaiter().GetResult()

let validGet = """{"caseReference":"WEB-V2-001"}"""

let validList = """{"limit":10}"""

let validResolve =
    $"""{{"operationId":"{operationId:D}","requestSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}"""
