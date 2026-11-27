module ClaimCore.Tests.VocabularyTests

open ClaimCore.Application.Contracts
open System
open ClaimCore.TestSupport
open Expecto
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type private Member =
    | Owner
    | Reader
    | Auditor

[<RequireQualifiedAccess>]
type private Payload =
    | Empty
    | Value of int

[<RequireQualifiedAccess>]
type private Colliding =
    | URLValue
    | UrlValue

let private all = [ Member.Owner; Member.Reader; Member.Auditor ]

let private token =
    function
    | Member.Owner -> "OWNER"
    | Member.Reader -> "READER"
    | Member.Auditor -> "AUDITOR"

let private vocabulary =
    Vocabulary.ofUnion<Member> "role" TokenCasing.ScreamingSnake None

[<Tests>]
let tests =
    testList
        "finite vocabularies"
        [
            testCase
                "derived spelling handles acronyms camel case and vocabulary prefixes"
                (fun () ->
                    Expect.equal
                        (Vocabulary.formatToken
                            TokenCasing.ScreamingSnake
                            (Some "OIDC_")
                            "HTTPHeader")
                        "OIDC_HTTP_HEADER"
                        "One prefix applies to the complete set"

                    Expect.equal
                        (Vocabulary.formatToken TokenCasing.CamelCase None "HTTPHeader")
                        "httpHeader"
                        "Camel-case acronym boundary"

                    Expect.equal
                        (Vocabulary.ofUnion<Member> "member" TokenCasing.CamelCase None
                         |> Vocabulary.tokens)
                        [ "owner"; "reader"; "auditor" ]
                        "Member names derive in declaration order")
            testCase "derived vocabularies reject payload unions and non-unions" (fun () ->
                Expect.throwsT<ArgumentException>
                    (fun () ->
                        Vocabulary.ofUnion<Payload> "payload" TokenCasing.ScreamingSnake None
                        |> ignore)
                    "A payload has no single vocabulary value"

                Expect.throwsT<ArgumentException>
                    (fun () ->
                        Vocabulary.ofUnion<int> "number" TokenCasing.ScreamingSnake None |> ignore)
                    "Numbers are not a declared closed set")
            testCase "derived and combined vocabularies reject token collisions" (fun () ->
                Expect.throwsT<ArgumentException>
                    (fun () ->
                        Vocabulary.ofUnion<Colliding> "collision" TokenCasing.ScreamingSnake None
                        |> ignore)
                    "Different case names can produce one token"

                Expect.throwsT<ArgumentException>
                    (fun () ->
                        Vocabulary.ofEntries
                            "combined"
                            [
                                Member.Owner, "SAME"
                                Member.Reader, "SAME"
                                Member.Auditor, "OTHER"
                            ]
                        |> ignore)
                    "Combined message namespaces remain unambiguous")
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
                "the declared task vocabulary covers all aggregate parts without duplicate identities"
                (fun () ->
                    let tokens = Vocabulary.tokens TaskVocabulary.vocabulary

                    Expect.hasLength
                        tokens
                        40
                        "All ledger declarations, including operator provisioning and recovery"

                    for subject, count in
                        [
                            ActSubject.Case, 13
                            ActSubject.People, 11
                            ActSubject.Work, 1
                            ActSubject.Note, 2
                            ActSubject.Attachment, 2
                            ActSubject.Party, 3
                            ActSubject.Link, 2
                            ActSubject.Reminder, 6
                        ] do
                        Expect.equal
                            (TaskVocabulary.count subject)
                            count
                            "Independent task inventory"

                    Expect.equal (List.distinct tokens) tokens "Task identities are unambiguous"

                    Expect.equal
                        (TaskId.token (TaskId.ofLiteral "people.revoke"))
                        "people.revoke"
                        "Stable declared identity")
        ]
