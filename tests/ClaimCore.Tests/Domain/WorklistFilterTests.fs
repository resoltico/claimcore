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
                        Inputs.registrationWith
                            [
                                BusinessField.ClaimantName, "ＲĪＧＡ%"
                                BusinessField.InsurerName, "A_B\\C"
                            ]
                            Inputs.registration
                        |> Inputs.admittedRegistration

                    let fields =
                        {
                            CaseFields.CaseReference = Inputs.reference "CASE-1"
                            Status = CaseStatus.Opened
                            Registration = registration
                            Progress = PaymentProgress.Undecided
                        }

                    Expect.equal
                        (CaseSearch.text fields)
                        "rīga%\u001fa_b\\c\u001fcase-1"
                        "Only declared searchable values are joined by U+001F"

                    Expect.equal
                        (CaseSearchText.parse "ＲĪＧＡ" |> admitted |> CaseSearchText.value)
                        "rīga"
                        "Full-width Latin and Latvian case normalize independently of current culture")
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
