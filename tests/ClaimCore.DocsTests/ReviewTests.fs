module ClaimCore.DocsTests.ReviewTests

open System
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Docs
open ClaimCore.DocsTests.Fixtures

let private declaration =
    {
        Id = "CC-DOM-001"
        Document = "docs/domain.md"
        Line = 3
        SectionBytes =
            Encoding.UTF8.GetBytes(
                "<a id=\"cc-dom-001\"></a>\n### CC-DOM-001 — Exact record\n\nNormative body.\n"
            )
    }

let private review whole spans hash =
    {
        ContractId = declaration.Id
        ReviewerKind = "agent"
        Reviewer = "independent QA"
        ReviewedOn = DateOnly(2026, 9, 9)
        Conclusion = "source-reviewed"
        ReviewSubjectHash = hash
        WholeFiles = whole
        MarkedSpans = spans
    }

let private wholeFileTest =
    testCase "whole-file changes invalidate the review subject"
    <| fun _ ->
        use repository = new TempRepository()
        repository.Write("docs/domain.md", "first\n") |> ignore
        let candidate = review [ "docs/domain.md" ] [] (String.replicate 64 "0")
        let first = Reviews.subjectHash repository.Root declaration candidate |> requireOk
        repository.Write("docs/domain.md", "second\n") |> ignore
        let second = Reviews.subjectHash repository.Root declaration candidate |> requireOk
        Expect.notEqual first second "Exact reviewed bytes participate in the hash"

let private contractBodyTest =
    testCase "normative contract-body changes invalidate the review subject"
    <| fun _ ->
        use repository = new TempRepository()
        repository.Write("src/file.fs", "reviewed\n") |> ignore
        let candidate = review [ "src/file.fs" ] [] (String.replicate 64 "0")

        let parsed body =
            markdown
                "docs/domain.md"
                ($"<a id=\"cc-dom-001\"></a>\n### CC-DOM-001 — Exact record\n\n{body}\n\n## Next\n")
            |> List.singleton
            |> Contracts.declarations
            |> requireOk
            |> List.exactlyOne

        let first =
            Reviews.subjectHash repository.Root (parsed "Normative body.") candidate
            |> requireOk

        let changed = parsed "Changed rule."
        let second = Reviews.subjectHash repository.Root changed candidate |> requireOk
        Expect.notEqual first second "The complete owned documentation section is reviewed"

let private markedSpanTest =
    testCase "marked spans ignore unrelated bytes but detect reviewed changes"
    <| fun _ ->
        use repository = new TempRepository()

        let span =
            {
                Path = "src/file.fs"
                BeginMarker = "review-subject:begin"
                EndMarker = "review-subject:end"
            }

        let candidate = review [] [ span ] (String.replicate 64 "0")

        repository.Write(
            "src/file.fs",
            "outside\nreview-subject:begin\ninside\nreview-subject:end\noutside\n"
        )
        |> ignore

        let first = Reviews.subjectHash repository.Root declaration candidate |> requireOk

        repository.Write(
            "src/file.fs",
            "changed\nreview-subject:begin\ninside\nreview-subject:end\nchanged\n"
        )
        |> ignore

        let unrelated =
            Reviews.subjectHash repository.Root declaration candidate |> requireOk

        Expect.equal unrelated first "Unregistered surroundings are outside this review"

        repository.Write(
            "src/file.fs",
            "changed\nreview-subject:begin\nnew\nreview-subject:end\nchanged\n"
        )
        |> ignore

        let changed = Reviews.subjectHash repository.Root declaration candidate |> requireOk
        Expect.notEqual changed first "Registered span changes invalidate review"

let private invalidSubjectTest =
    testCase "review registry cannot include itself or an empty subject"
    <| fun _ ->
        use repository = new TempRepository()

        review [ ReviewRegistry.path ] [] (String.replicate 64 "0")
        |> Reviews.subjectHash repository.Root declaration
        |> requireError
        |> ignore

        review [] [] (String.replicate 64 "0")
        |> Reviews.subjectHash repository.Root declaration
        |> requireError
        |> ignore

let private vacuousRegistryTest =
    testCase "strict verification rejects a vacuous review registry"
    <| fun _ ->
        use repository = new TempRepository()

        repository.Write(ReviewRegistry.path, "{\"schemaVersion\":2,\"reviews\":[]}")
        |> ignore

        Reviews.verify repository.Root [ declaration ] (DateOnly(2026, 9, 9))
        |> requireError
        |> ignore

let private registryEntry conclusion =
    JsonSerializer.Serialize(
        {|
            schemaVersion = 2
            reviews =
                [|
                    {|
                        contractId = "CC-DOM-001"
                        reviewerKind = "agent"
                        reviewer = "Agent source review; not owner authorization"
                        reviewedOn = "2026-09-22"
                        conclusion = conclusion
                        reviewSubjectHash = String.replicate 64 "a"
                        wholeFiles = [| "src/file.fs" |]
                        markedSpans = Array.empty<string>
                    |}
                |]
        |}
    )

let private sourceReviewOnly =
    testCase "source review is not accepted as independent owner approval" (fun () ->
        use repository = new TempRepository()
        repository.Write(ReviewRegistry.path, registryEntry "source-reviewed") |> ignore
        ReviewRegistry.load repository.Root |> requireOk |> ignore
        repository.Write(ReviewRegistry.path, registryEntry "approved") |> ignore
        ReviewRegistry.load repository.Root |> requireError |> ignore)

let private legacyRegistry =
    testCase "legacy approval registry schema is refused without compatibility fallback" (fun () ->
        use repository = new TempRepository()

        repository.Write(ReviewRegistry.path, "{\"schemaVersion\":1,\"reviews\":[]}")
        |> ignore

        ReviewRegistry.load repository.Root |> requireError |> ignore)


let tests =
    testList
        "semantic review subjects"
        [
            sourceReviewOnly
            legacyRegistry
            wholeFileTest
            contractBodyTest
            markedSpanTest
            invalidSubjectTest
            vacuousRegistryTest
        ]
