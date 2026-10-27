module ClaimCore.Tests.VocabularyTests

open System
open Expecto
open ClaimCore.Contract
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type private Member =
    | Owner
    | Reader
    | Auditor

let private all = [ Member.Owner; Member.Reader; Member.Auditor ]

let private token =
    function
    | Member.Owner -> "OWNER"
    | Member.Reader -> "READER"
    | Member.Auditor -> "AUDITOR"

let private vocabulary = Vocabulary.define "role" all token

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
                    (fun () -> Vocabulary.define "role" [ Member.Owner ] token |> ignore)
                    "A missing union variant cannot ship")
            testCase
                "construction refuses duplicate tokens with complete variant coverage"
                (fun () ->
                    Expect.throwsT<ArgumentException>
                        (fun () -> Vocabulary.define "role" all (fun _ -> "X") |> ignore)
                        "Ambiguous token")
            testCase
                "construction refuses duplicate cases with complete variant coverage"
                (fun () ->
                    Expect.throwsT<ArgumentException>
                        (fun () ->
                            Vocabulary.define "role" (all @ [ Member.Reader ]) token |> ignore)
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
                            Vocabulary.define "act" [ CaseActKind.Open ] CaseActKinds.token
                            |> ignore)
                        "Missing stewardship variant")
            testCase "one act vocabulary covers disjoint case and authority tokens" (fun () ->
                let tokens = Vocabulary.tokens ActKinds.vocabulary

                Expect.equal
                    (List.length tokens)
                    18
                    "Twelve case and six authority acts, including owner provisioning"

                Expect.equal (List.distinct tokens) tokens "Scopes have no ambiguous act spelling"

                Expect.equal
                    (Vocabulary.messageKey
                        ActKinds.vocabulary
                        (ActKind.Authority AuthorityKind.Revoke)
                        "label")
                    "act.REVOKE.label"
                    "Shared act message family")
        ]
