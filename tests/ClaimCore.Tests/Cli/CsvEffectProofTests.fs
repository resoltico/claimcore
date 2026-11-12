module ClaimCore.Tests.CsvEffectProofTests

open System
open System.IO
open System.Text
open Microsoft.VisualBasic.FileIO
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Cli
open ClaimCore.TestSupport

let private parse bytes =
    use source = new StringReader(Encoding.UTF8.GetString(bytes: byte array))
    use reader = new TextFieldParser(source)
    reader.SetDelimiters [| "," |]
    reader.HasFieldsEnclosedInQuotes <- true
    let header = reader.ReadFields() |> nonNull
    let row = reader.ReadFields() |> nonNull
    Expect.equal row.Length header.Length "Every field has one header"
    List.zip (List.ofArray header) (List.ofArray row) |> Map.ofList

[<Tests>]
let tests =
    testCase "CSV exports preserve operation proof and distinct effect provenance" (fun () ->
        let raw = Guid.Parse "019a6b00-0000-7000-8000-000000000001"
        let operation = CaseFixtures.operationId raw
        let actor = Inputs.actorReference (CaseFixtures.actorId raw)
        let hash = EntryHash.ofBytes (Array.create 32 0xabuy) |> Option.get
        let position = EffectPosition.create (CaseFixtures.sequence 7L) 3 |> Option.get

        let entry: AuthorityHistoryEntry =
            {
                OperationId = operation
                Position = position
                OperationHash = hash
                RecordedAt = DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero)
                RecordedBy = actor
                Target = actor
                Facts = AuthorityHistoryFacts.Enabled
            }

        let authority =
            Array.append AuthorityHistoryCsv.csvHeader (AuthorityHistoryCsv.csvRows [ entry ])
            |> parse

        let timeline: CaseTimelineEntry =
            {
                OperationId = operation
                Position = position
                OperationHash = hash
                Subject = TimelineSubject.CaseFact(Revision.first, CaseActKind.Open)
                RecordedAt = entry.RecordedAt
                RecordedBy = actor
                Changes = TimelineChanges.Withheld
            }

        let activity =
            Array.append
                TimelineCsv.csvHeader
                (TimelineCsv.csvRows
                    {
                        Disclosure = HistoryDisclosure.Full
                        Basis = Inputs.workBasis
                        CaseId = CaseFixtures.caseId raw
                        Reference = Inputs.reference "CASE-PROOF"
                        Items = [ timeline ]
                        NextCursor = None
                    })
            |> parse

        for row in [ authority; activity ] do
            Expect.equal row["operationId"] (raw.ToString "D") "Stable operation identity"
            Expect.equal row["sequence"] "7" "Sequence is the operation position"
            Expect.equal row["effectIndex"] "3" "Effect ordinal is retained independently"

            Expect.equal
                row["operationHash"]
                (String.replicate 32 "ab")
                "Hash belongs to complete root")
