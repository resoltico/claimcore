module ClaimCore.Tests.StructuralAdmissionTests

open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.Wire

type private Counted = { Count: int64 }
let private bytes (value: string) = Encoding.UTF8.GetBytes value

[<Tests>]
let tests =
    testList
        "structural admission and semantic interpretation"
        [
            testCase "structural admission never invokes a semantic record constructor" (fun () ->
                let mutable constructed = false

                let codec =
                    (Codec.record (fun count ->
                        constructed <- true

                        if count % 2L = 0L then
                            Ok { Count = count }
                        else
                            Error "Additional semantic condition")
                    : Fields<Counted, _>)
                    |> Codec.field "count" _.Count (Codec.integer 0L 10L)
                    |> Codec.buildResult (fun path reason -> { Path = path; Reason = reason })

                let input = bytes """{"count":3}"""

                Expect.isOk
                    (Codec.validate 512 codec input)
                    "An odd exact integer satisfies the declared structure"

                Expect.isFalse
                    constructed
                    "No semantic constructor executes during structural validation"

                Expect.isError
                    (Codec.decode 512 codec input)
                    "Typed interpretation retains its separate condition"

                Expect.isTrue constructed "The semantic interpreter actually ran"
                constructed <- false

                for raw in
                    [
                        """{"count":null}"""
                        """{"count":"3"}"""
                        """{"count":11}"""
                        """{"count":3,"extra":false}"""
                        "{}"
                    ] do
                    Expect.isError
                        (Codec.validate 512 codec (bytes raw))
                        "Independent structural mutations refuse"

                Expect.isFalse constructed "Structural failures never manufacture typed values")
            testCase
                "the authored financial differential is structural data but fails core semantics"
                (fun () ->
                    let raw =
                        bytes
                            """{"entries":[{"kind":"RECORDED","entry":{"paymentId":"019fc6e8-1813-7000-8000-000000000001","paymentDate":"2026-11-08","paidAmount":"1"}}],"before":null,"after":{"payable":{"amount":"100","currency":"EUR"},"paid":{"amount":"1","currency":"EUR"},"remaining":{"amount":"98","currency":"EUR"}}}"""

                    Expect.isOk
                        (Codec.validate Limits.MaximumResponseBytes PaymentCodecs.changes raw)
                        "No financial equation is added to structural validation"

                    Expect.isError
                        (Codec.decode Limits.MaximumResponseBytes PaymentCodecs.changes raw)
                        "The core-owned totals constructor refuses the inconsistent equation"

                    let corrected =
                        Encoding.UTF8.GetString(raw).Replace("\"98\"", "\"99\"") |> bytes

                    Expect.isOk
                        (Codec.decode Limits.MaximumResponseBytes PaymentCodecs.changes corrected)
                        "Correcting only the independent total admits the same wire structure"

                    let money amount =
                        Money.create
                            (Amount.parse amount
                             |> Result.defaultWith (fun error -> failtestf "%A" error))
                            (Currency.parse "EUR"
                             |> Result.defaultWith (fun error -> failtestf "%A" error))

                    Expect.isError
                        (PaymentTotals.create (money "100") (money "1") (money "98"))
                        "Independent core admission"

                    Expect.isOk
                        (PaymentTotals.create (money "100") (money "1") (money "99"))
                        "The exact equation admits at its owner")
            testCase
                "the structural byte boundary retains Unicode duplicate-key and integer safeguards"
                (fun () ->
                    let codec =
                        Codec.record id
                        |> Codec.field "count" id (Codec.integer 0L 9007199254740991L)
                        |> Codec.build

                    for raw in
                        [
                            """{"count":9007199254740992}"""
                            """{"count":1,"\u0063ount":2}"""
                            """{"count":1.5}"""
                        ] do
                        Expect.isError
                            (Codec.validate 512 codec (bytes raw))
                            "Strict byte and exact-integer admission"

                    Expect.isOk
                        (Codec.validate 512 codec (bytes """{"count":9007199254740991}"""))
                        "Largest exact integer"

                    let one = Codec.boundedText 1 1

                    Expect.isOk
                        (Codec.validate 512 one (bytes "\"🙂\""))
                        "One supplementary Unicode scalar"

                    Expect.isError
                        (Codec.validate 512 one (bytes "\"🙂🙂\""))
                        "Two scalars exceed the declared bound"

                    Expect.isError
                        (Codec.validate 512 (Codec.boundedUtf8Text 1 3) (bytes "\"🙂\""))
                        "Four UTF-8 bytes exceed three"

                    for raw in [ "\"\\uD800\""; "\"\\uFDD0\""; "\"\\u0000\"" ] do
                        Expect.isError
                            (Codec.validate 512 Codec.text (bytes raw))
                            "Unrepresentable scalar text refuses"

                    Expect.isError
                        (Codec.validate 512 Codec.text [| 0x22uy; 0xC3uy; 0x28uy; 0x22uy |])
                        "Malformed UTF-8 refuses")
            testCase
                "structural rendering guards refuse duplicate roles and incoherent tables"
                (fun () ->
                    let entry = """{"kind":"ENTRIES","entries":[]}"""

                    for role, expected in [ "WORK", false; "FACTS", true ] do
                        let raw =
                            sprintf
                                """{"regions":[{"role":"%s","content":%s},{"role":"%s","content":%s}]}"""
                                role
                                entry
                                role
                                entry

                        Expect.equal
                            (Codec.validate 8192 PresentationCodecs.view (bytes raw) |> Result.isOk)
                            expected
                            "Only fact regions repeat"

                    let column =
                        """{"key":"one","labelId":"label.details","prominence":"PRIMARY","isMoney":false}"""

                    let row = """{"key":"row-a","values":[{"kind":"TEXT","value":"Authored"}]}"""

                    for columns, rows, expected in
                        [
                            column, row, true
                            column + "," + column, "", false
                            "", row, false
                            column, row + "," + row, false
                        ] do
                        let raw =
                            sprintf
                                """{"regions":[{"role":"RESULTS","content":{"kind":"TABLE","columns":[%s],"rows":[%s]}}]}"""
                                columns
                                rows

                        Expect.equal
                            (Codec.validate 8192 PresentationCodecs.view (bytes raw) |> Result.isOk)
                            expected
                            "Unique column and row identities and one value per column")
            testCase "structural message arguments cannot silently overwrite one another" (fun () ->
                let argument = """{"key":"canary","value":{"kind":"TEXT","value":"Core-owned"}}"""

                for arguments, expected in [ argument, true; argument + "," + argument, false ] do
                    let raw =
                        sprintf
                            """{"regions":[{"role":"RESULTS","content":{"kind":"TABLE","columns":[],"rows":[],"meter":{"current":{"amount":"1","currency":"EUR","displayScale":2},"target":{"amount":"100","currency":"EUR","displayScale":2},"complete":false,"description":{"messageId":"label.details","arguments":[%s]}}}}]}"""
                            arguments

                    Expect.equal
                        (Codec.validate 8192 PresentationCodecs.view (bytes raw) |> Result.isOk)
                        expected
                        "Message argument identity is structural")
            testCase
                "raw principal input and canonical principal projection have explicit admission boundaries"
                (fun () ->
                    let raw =
                        bytes
                            """{"operationId":"019aa6a0-0000-7000-8000-000000000001","task":"people.register","binding":{"kind":"PEOPLE"},"inputs":{"input:principal.kind":"HUMAN","input:principal.issuer":"https://issuer.example:bad/","input:principal.subject":"person","input:profile.displayName":"Person"}}"""

                    Expect.isOk
                        (Codec.validate 8192 OperationSubmissionCodecs.submission raw)
                        "Raw authored input is structurally admissible"

                    match Codec.decode 8192 OperationSubmissionCodecs.submission raw with
                    | Ok submission ->
                        let request =
                            match ClaimCore.TestSupport.Submissions.command submission with
                            | ClaimCore.Application.Contracts.OperationInput.People request ->
                                request
                            | _ -> failtest "People required"

                        Expect.isError
                            (People.parse request)
                            "Canonical principal admission remains at the core"
                    | other ->
                        failtestf "Expected structurally admitted people input, got %A" other

                    let page =
                        bytes
                            """{"basis":{"observedAt":"2026-11-09T12:00:00.000000Z","businessDate":"2026-11-09","timeZone":"Etc/UTC"},"items":[{"principal":{"kind":"HUMAN","issuer":"https://issuer.example:bad/","subject":"person"},"firstSeen":"2026-11-09T12:00:00.000000Z","lastSeen":"2026-11-09T12:00:00.000000Z","claims":{}}],"nextCursor":null}"""

                    Expect.isOk
                        (Codec.validate 8192 ResponseCodecs.accessRequestsPage page)
                        "The declared projection structure does not invent URI canonicality"

                    let source = Encoding.UTF8.GetString page

                    for issuer in
                        [ "https://issuer.example/\u0085"; "https://issuer.example/\uFEFF" ] do
                        let changed =
                            source.Replace("https://issuer.example:bad/", issuer) |> bytes

                        Expect.isError
                            (Codec.validate 8192 ResponseCodecs.accessRequestsPage changed)
                            "Native and generated issuer grammar excludes the same control and whitespace scalars"

                    Expect.isOk
                        (Codec.decode
                            8192
                            ResponseCodecs.accessRequestsPage
                            (source.Replace(
                                "https://issuer.example:bad/",
                                "https://issuer.example/"
                             )
                             |> bytes))
                        "The same projection with a canonical issuer is a positive control"

                    Expect.isError
                        (Codec.decode 8192 ResponseCodecs.accessRequestsPage page)
                        "Constructing a trusted principal still requires canonical admission")

        ]
