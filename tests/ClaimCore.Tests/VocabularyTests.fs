module ClaimCore.Tests.VocabularyTests

open System
open Expecto
open ClaimCore.Contract

[<RequireQualifiedAccess>]
type private Member =
    | Owner
    | Reader
    | Auditor

let private definitions =
    [ Member.Owner, "OWNER"; Member.Reader, "READER"; Member.Auditor, "AUDITOR" ]

let private vocabulary = Vocabulary.define "role" definitions

[<Tests>]
let tests =
    testList
        "finite vocabularies"
        [
            testCase "one ordered definition owns tokens parsing and message keys" (fun () ->
                Expect.equal
                    (Vocabulary.tokens vocabulary)
                    [ "OWNER"; "READER"; "AUDITOR" ]
                    "Declaration order"

                Expect.equal (Vocabulary.token vocabulary Member.Reader) "READER" "One token"

                Expect.equal
                    (Vocabulary.tryParse vocabulary "READER")
                    (Some Member.Reader)
                    "Exact parsing"

                Expect.isNone (Vocabulary.tryParse vocabulary "reader") "No spelling normalization"

                Expect.equal
                    (Vocabulary.messageKey vocabulary Member.Reader "label")
                    "role.READER.label"
                    "Derived key")
            testCase "construction refuses missing union variants" (fun () ->
                Expect.throwsT<ArgumentException>
                    (fun () -> Vocabulary.define "role" [ Member.Owner, "OWNER" ] |> ignore)
                    "A missing union variant cannot ship")
            testCase
                "construction refuses duplicate tokens with complete variant coverage"
                (fun () ->
                    Expect.throwsT<ArgumentException>
                        (fun () ->
                            Vocabulary.define
                                "role"
                                [ Member.Owner, "X"; Member.Reader, "X"; Member.Auditor, "A" ]
                            |> ignore)
                        "Ambiguous token")
            testCase
                "construction refuses duplicate cases with complete variant coverage"
                (fun () ->
                    Expect.throwsT<ArgumentException>
                        (fun () ->
                            Vocabulary.define
                                "role"
                                (definitions @ [ Member.Reader, "ANOTHER_READER" ])
                            |> ignore)
                        "Ambiguous case")
            testCase
                "payload variants are checked without treating payload values as variants"
                (fun () ->
                    Expect.equal
                        (Vocabulary.all CaseActKinds.vocabulary |> List.length)
                        12
                        "Both payload variants cover all derived acts"

                    Expect.throwsT<ArgumentException>
                        (fun () ->
                            Vocabulary.define
                                "act"
                                [ CaseActKind.Business BusinessActKind.Open, "OPEN" ]
                            |> ignore)
                        "Missing stewardship variant")
        ]
