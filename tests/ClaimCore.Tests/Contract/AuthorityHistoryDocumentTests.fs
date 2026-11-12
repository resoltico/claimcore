module ClaimCore.Tests.AuthorityHistoryDocumentTests

open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport

let private parent = ResponseExamples.caseRead.Snapshot.CaseId

let private entry index facts =
    {
        AuthorityHistoryEntry.OperationId = ResponseExamples.receipt.OperationId
        Position = EffectPosition.create Sequence.first index |> Option.get
        OperationHash = EntryHash.zero
        RecordedAt = Inputs.workBasis.ObservedAt
        RecordedBy = ResponseExamples.caseRead.RecordedBy
        Target = ResponseExamples.caseRead.RecordedBy
        Facts = facts
    }

[<Tests>]
let tests =
    testCase "authority history destinations bind only the authorized scoped effects" (fun () ->
        let document =
            HumanReadDocuments.history
                {
                    Basis = Inputs.workBasis
                    Entries =
                        [
                            entry
                                0
                                (AuthorityHistoryFacts.Granted
                                    {
                                        Role = Role.CaseReader
                                        Scope =
                                            GrantScope.Case(
                                                parent,
                                                ResponseExamples.caseRead.Snapshot.Reference
                                            )
                                    })
                            entry 1 AuthorityHistoryFacts.Enabled
                        ]
                    NextCursor = Some "history-next"
                }

        match document.Context with
        | InteractionContext.AuthorityHistory controls ->
            Expect.equal
                (controls.Entries |> List.map _.Key)
                [ "1/0"; "1/1" ]
                "Actual effect positions"

            Expect.equal
                (controls.Entries |> List.map _.CaseId)
                [ Some parent; None ]
                "Only scoped effect has a destination"
        | _ -> failtest "Authority history context required"

        Expect.equal
            (HumanDocument.nextCursor document)
            (Some "history-next")
            "Native cursor traversal"

        let encoded = Codec.encode HumanDocumentCodecs.document document

        Expect.isOk
            (Codec.decode 65536 HumanDocumentCodecs.document encoded)
            "Native document boundary"

        let invalid = JsonNode.Parse(System.Text.Encoding.UTF8.GetString encoded) |> nonNull
        let context = nonNull invalid["context"]
        let entries = nonNull context["entries"]
        let first = nonNull entries[0]
        first["key"] <- System.Text.Json.Nodes.JsonValue.Create "different"

        Expect.isError
            (Codec.decode
                65536
                HumanDocumentCodecs.document
                (System.Text.Encoding.UTF8.GetBytes(invalid.ToJsonString())))
            "Unbound destination refused")
