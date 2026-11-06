module ClaimCore.Tests.ReviewFacetFixtureTests

open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Protocol.Presentation
open ClaimCore.Json
open ClaimCore.TestSupport

let private corpus () =
    File.ReadAllBytes(
        Path.Combine(RepositoryRoot.find (), "tests/fixtures/wire-response-examples.json")
    )
    |> JsonDocument.Parse

let private presented (row: JsonElement) =
    match
        Codec.decode
            Limits.MaximumResponseBytes
            Api.operationPreview.Output
            (Encoding.UTF8.GetBytes(row.GetProperty("value").GetRawText()))
    with
    | Ok {
             Outcome = Outcome.Succeeded(PresentedPreviewed.Reviewed review)
         } -> review
    | other -> failtestf "Expected admitted reviewed literal: %A" other

let private coreSubject =
    function
    | PresentedReviewSubject.Case value ->
        let act: ValidCaseAct =
            if CaseState.lifecycle value.Proposed.State = Lifecycle.Erased then
                CaseAct.Bare BareAct.Erase
            else
                CaseAct.Correct
                    {
                        Registration = Amendment.Keep
                        Decision = Change.Keep
                        Payment = PaymentChange.Keep
                    }

        OperationReview.Case
            {
                Before = value.Before
                Proposed = value.Proposed
                Context = value.Context
                // The authoritative comparison owner computes typed changes; the literal facet remains an independent oracle.
                Changes = CaseChanges.review act value.Before value.Proposed
            }
    | PresentedReviewSubject.Work value -> OperationReview.Work value
    | PresentedReviewSubject.Reminder value -> OperationReview.Reminder value
    | PresentedReviewSubject.Authority value -> OperationReview.Authority value

[<Tests>]
let tests =
    testList
        "independent review presentation fixtures"
        [
            testCase
                "literal case work compound admission and profile facets match authoritative meaning"
                (fun () ->
                    let expectedNames =
                        Set.ofList
                            [
                                "operation.preview.case-response-0"
                                "review-all-fields"
                                "review-case-erase-payload-fields"
                                "work-review"
                                "reminder-review-CREATED"
                                "reminder-review-CHANGED"
                                "reminder-review-COMPLETED_AND_PLANNED"
                                "operation.preview.authority-reviewed"
                                "authority-scoped-dormant-overlap-review"
                                "authority-admit-ordered-effects"
                                "private-provisioning-review-effect-grammar"
                                "authority-erase-profile-effect"
                                "authority-revoke-effect"
                                "authority-disable-effect"
                                "authority-set-profile-saved-facts"
                            ]

                    use document = corpus ()
                    let mutable exercised = Set.empty

                    for row in document.RootElement.GetProperty("cases").EnumerateArray() do
                        let name = row.GetProperty("name").GetString() |> nonNull

                        if Set.contains name expectedNames then
                            let supplied = presented row
                            let actual = PresentedReview.ofCore (coreSubject supplied.Subject)

                            Expect.equal
                                actual.Presentation
                                supplied.Presentation
                                (name
                                 + " literal rows pin meaning, ordering, labels and exact precision")

                            exercised <- Set.add name exercised

                    Expect.equal
                        exercised
                        expectedNames
                        "Every declared semantic fixture was actually exercised")
            testCase
                "supplied structural facet survives native decode and write without reconstruction"
                (fun () ->
                    use document = corpus ()

                    let row =
                        document.RootElement.GetProperty("cases").EnumerateArray()
                        |> Seq.find (fun row ->
                            row.GetProperty("name").GetString() =
                                "supplied-review-all-semantic-value-tags")

                    let supplied = presented row

                    let raw =
                        row
                            .GetProperty("value")
                            .GetProperty("outcome")
                            .GetProperty("data")
                            .GetProperty("review")
                            .GetProperty("presentation")

                    let encoded =
                        PresentationCodecs.review.Write supplied.Presentation
                        |> WireWriter.bytes
                        |> Encoding.UTF8.GetString
                        |> JsonNode.Parse

                    Expect.isTrue
                        (JsonNode.DeepEquals(encoded, JsonNode.Parse(raw.GetRawText())))
                        "No supplied metadata is replaced from the body"

                    match supplied.Presentation.Changes.Head.After with
                    | PresentedValue.Money value ->
                        Expect.equal
                            value.DisplayScale
                            4
                            "The provided EUR scale is not recomputed to two"

                        Expect.equal
                            (ClaimCore.Cli.TextDescriptorValue.render
                                "en"
                                supplied.Presentation.Changes.Head.After)
                            "1.0000 EUR"
                            "The real human renderer consumes supplied precision"
                    | _ -> failtest "Expected the independently supplied money control")
        ]
