module ClaimCore.Tests.WorklistFilterTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.TestSupport

let private admitted result =
    Result.defaultWith (fun _ -> failtest "The authored search fixture must admit") result

[<Tests>]
let tests =
    testList
        "worklist search and facets"
        [
            testCase "search admission counts Unicode scalars and keeps name policy" (fun () ->
                Expect.isOk
                    (CaseSearchText.parse (String.replicate 100 "🙂"))
                    "One hundred supplementary scalars fit"

                Expect.equal
                    (CaseSearchText.parse (String.replicate 101 "🙂"))
                    (Error(Violation.TooLong 100))
                    "The limit counts scalars rather than UTF-16 units"

                for raw, problem in
                    [
                        "", Violation.TextRequired
                        " x", Violation.SurroundingWhitespace
                        "x\n", Violation.SurroundingWhitespace
                        "x\u0000y", Violation.MalformedUnicode
                        "x\u0001y", Violation.ControlCharacters
                        "x\u200by", Violation.FormattingCharacter
                    ] do
                    Expect.equal
                        (CaseSearchText.parse raw)
                        (Error problem)
                        "Search uses the name admission rules")
            testCase
                "stored text and admitted queries use the same NFKC invariant lowercase rule"
                (fun () ->
                    let registration =
                        DomainInputs.registrationWith
                            [
                                BusinessField.PolicyNumber, "ＲĪＧＡ%"
                                BusinessField.InsurerClaimReference, "A_B\\C"
                            ]
                            DomainInputs.registration
                        |> DomainInputs.admittedRegistration

                    let fields =
                        {
                            CaseFields.CaseReference = DomainInputs.reference "CASE-1"
                            Handling = Handling.Open
                            Registration = registration
                            Finance = FinancialPosition.undecided
                        }

                    Expect.equal
                        (CaseSearch.text [] fields)
                        "rīga%\u001fa_b\\c\u001fcase-1"
                        "Only declared searchable values are joined by U+001F"

                    Expect.equal
                        (CaseFields.value BusinessField.PolicyNumber fields)
                        (Some "ＲĪＧＡ%")
                        "Field lookup preserves authored claimant spelling"

                    Expect.equal
                        (CaseFields.value BusinessField.InsurerClaimReference fields)
                        (Some "A_B\\C")
                        "Field lookup preserves authored insurer spelling"

                    Expect.equal
                        (CaseFields.value BusinessField.IncidentDescription fields)
                        None
                        "An absent description remains absent"

                    for description, expected in
                        [
                            "ＡＢＣ\nالعربية 😀", "abc\nالعربية 😀\u001frīga%\u001fa_b\\c\u001fcase-1"
                            String.replicate 600 "X",
                            String.replicate 600 "x" + "\u001frīga%\u001fa_b\\c\u001fcase-1"
                        ] do
                        let described =
                            { fields with
                                Registration =
                                    Registration.withDescription
                                        (Some(IncidentDescription.parse description |> admitted))
                                        registration
                            }

                        Expect.equal
                            (CaseFields.value BusinessField.IncidentDescription described)
                            (Some description)
                            "Field lookup preserves complete authored description text"

                        Expect.equal
                            (CaseSearch.text [] described)
                            expected
                            "Search keeps descriptor order and exact NFKC text with paragraphs"

                    Expect.equal
                        (CaseSearchText.parse "ＲĪＧＡ" |> admitted |> CaseSearchText.value)
                        "rīga"
                        "Full-width Latin and Latvian case normalize independently of current culture")
            testCase "range admission identifies each upper field" (fun () ->
                let date raw = CalendarDate.parse raw |> admitted

                let early = date "2026-02-01"
                let late = date "2026-02-02"

                let admit dates =
                    { CaseListFilter.empty<string> with
                        Dates = Map.ofList dates
                    }
                    |> CaseListFilter.admit

                for lower, upper in
                    [
                        CaseDateFilter.RegisteredFrom, CaseDateFilter.RegisteredThrough
                        CaseDateFilter.ReceivedFrom, CaseDateFilter.ReceivedThrough
                        CaseDateFilter.ClosedFrom, CaseDateFilter.ClosedThrough
                    ] do
                    Expect.equal
                        (admit [ lower, late; upper, early ])
                        (Error(CaseSelectionIssue.ReversedRange upper))
                        "A reversed pair identifies its upper field"

                    for dates in
                        [ [ lower, early; upper, early ]; [ lower, late ]; [ upper, early ] ] do
                        Expect.isOk (admit dates) "Both equal and single-sided bounds admit"

                Expect.isOk
                    (admit [ CaseDateFilter.InactiveSince, early ])
                    "Inactive-since has no other endpoint")
            testCase "facet lists act as normalized sets and combine conjunctively" (fun () ->
                let filter =
                    CaseListFilter.create
                        [ Lifecycle.Active; Lifecycle.Active ]
                        [ CaseStatus.Opened ]
                        [ CaseProgress.Decided ]
                        (Some "RĪGA")
                        CaseListOrder.Recent
                    |> CaseListFilter.admit
                    |> admitted

                Expect.equal
                    filter.Lifecycles
                    [ Lifecycle.Active ]
                    "Repeated choices have one canonical meaning"

                Expect.isTrue
                    (CaseListFilter.matches
                        filter
                        Lifecycle.Active
                        (Some CaseStatus.Opened)
                        (Some CaseProgress.Decided)
                        (Some "rīga\u001finsurer\u001fcase-1"))
                    "Every selected facet and text matches"

                Expect.isFalse
                    (CaseListFilter.matches
                        filter
                        Lifecycle.Voided
                        (Some CaseStatus.Opened)
                        (Some CaseProgress.Decided)
                        (Some "rīga"))
                    "A lifecycle miss is not hidden by matching text"

                Expect.isFalse
                    (CaseListFilter.matches
                        filter
                        Lifecycle.Active
                        (Some CaseStatus.Closed)
                        (Some CaseProgress.Decided)
                        (Some "rīga"))
                    "Status is independent"

                Expect.isFalse
                    (CaseListFilter.matches
                        filter
                        Lifecycle.Active
                        (Some CaseStatus.Opened)
                        (Some CaseProgress.Paid)
                        (Some "rīga"))
                    "Progress is independent"

                Expect.isTrue
                    (CaseListFilter.matches
                        CaseListFilter.empty<CaseSearchText>
                        Lifecycle.Erased
                        None
                        None
                        None)
                    "Absent filters include erased identities")
        ]
