module ClaimCore.Tests.VocabularyTests

open System
open Expecto
open ClaimCore.Protocol
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
            testCase "explicit vocabularies still reject missing act cases" (fun () ->
                Expect.equal
                    (Vocabulary.all CaseActKind.vocabulary |> List.length)
                    12
                    "All twelve nullary acts are declared"

                Expect.throwsT<ArgumentException>
                    (fun () ->
                        Vocabulary.define "act" [ CaseActKind.Open ] CaseActKind.token |> ignore)
                    "Missing act cases")
            testCase "one act vocabulary covers disjoint case and authority tokens" (fun () ->
                let tokens = Vocabulary.tokens ActKind.vocabulary

                Expect.equal
                    (List.length tokens)
                    18
                    "Twelve case and six authority acts, including owner provisioning"

                Expect.equal (List.distinct tokens) tokens "Scopes have no ambiguous act spelling"

                Expect.equal
                    (Vocabulary.messageKey
                        ActKind.vocabulary
                        (ActKind.Authority AuthorityKind.Revoke)
                        "label")
                    "act.REVOKE.label"
                    "Shared act message family")
        ]
