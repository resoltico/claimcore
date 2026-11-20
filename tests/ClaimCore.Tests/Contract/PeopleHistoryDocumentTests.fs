module ClaimCore.Tests.PeopleHistoryDocumentTests

open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport

let private parent = ResponseExamples.caseRead.Snapshot.CaseId

let private entry index task changes =
    {
        PeopleHistoryEntry.OperationId = ResponseExamples.receipt.OperationId
        Position = EffectPosition.create Sequence.first index |> Option.get
        OperationHash = EntryHash.zero
        RecordedAt = DomainInputs.workBasis.ObservedAt
        RecordedBy = AuthorityRef.Actor ResponseExamples.caseRead.RecordedBy
        Target =
            DomainInputs.namedActorReference
                (CaseFixtures.actorId (System.Guid.Parse "20000000-0000-4000-8000-000000000009"))
                "Affected account"
        Task = TaskId.ofLiteral task
        Changes = changes
        PrivateAvailable = true
        References =
            {
                Actors = []
                Cases =
                    changes.Grants
                    |> List.choose (fun grant ->
                        match grant.Grant.Scope with
                        | Scope.Case id ->
                            Some
                                {
                                    CaseId = id
                                    Reference = ResponseExamples.snapshot.Reference
                                }
                        | Scope.Installation -> None)
            }
    }

[<Tests>]
let tests =
    testCase
        "people history links the affected account independently of scoped grant changes"
        (fun () ->
            let document =
                HumanReadDocuments.history
                    {
                        Basis = DomainInputs.workBasis
                        Entries =
                            [
                                entry
                                    0
                                    "people.grant"
                                    {
                                        PersonPartDescription.Profiles =
                                            { Before = None; After = None }
                                        Principal = None
                                        Enabled = None
                                        ProfilePresent = None
                                        DisplayName = None
                                        DigestEmail = None
                                        Contact = None
                                        Grants =
                                            [
                                                {
                                                    Grant =
                                                        {
                                                            Role = Role.CaseReader
                                                            Scope = Scope.Case parent
                                                        }
                                                    Before = false
                                                    After = true
                                                }
                                            ]
                                    }
                                entry
                                    1
                                    "people.enable"
                                    {
                                        PersonPartDescription.Profiles =
                                            { Before = None; After = None }
                                        Principal = None
                                        Enabled =
                                            Some
                                                {
                                                    Before = Some false
                                                    After = Some true
                                                }
                                        ProfilePresent = None
                                        DisplayName = None
                                        DigestEmail = None
                                        Contact = None
                                        Grants = []
                                    }
                            ]
                        NextCursor = Some "history-next"
                    }

            let rows =
                document.Screen.Regions
                |> List.collect (fun region ->
                    match region.Content with
                    | RegionContent.Timeline entries -> entries
                    | _ -> [])

            let operation = OperationId.format ResponseExamples.receipt.OperationId

            Expect.equal
                (rows |> List.map _.Key)
                [ "1/0." + operation; "1/1." + operation ]
                "Both effects of one operation have independent row identities"

            Expect.equal
                (rows |> List.map _.Link)
                (List.replicate
                    2
                    (Some(
                        Link.person (
                            CaseFixtures.actorId (
                                System.Guid.Parse "20000000-0000-4000-8000-000000000009"
                            )
                        )
                    )))
                "Both rows refer to the affected account independently of the grant scope"

            Expect.equal document.NextCursor (Some "history-next") "Cursor belongs to the document"

            Expect.isOk
                (Codec.encode HumanDocumentCodecs.document document
                 |> Codec.decode 65536 HumanDocumentCodecs.document)
                "Actual native boundary")
