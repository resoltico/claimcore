module ClaimCore.Tests.RecordBoundaryTests

open System
open System.Globalization
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.Tests.Fixtures

let private utf8 (value: string) = Encoding.UTF8.GetBytes value
let private snapshot () = Case.snapshot (opened ())

[<Tests>]
let tests =
    testList
        "current retained snapshot boundaries"
        [
            testCase "accepted snapshot revisions retain exact safe integer lexemes" (fun () ->
                let terminal =
                    { snapshot () with
                        Revision = CaseFixtures.revision 9007199254740991L
                    }

                let encoded = CaseRecord.encodeSnapshot terminal

                Expect.equal
                    (CaseRecord.decodeSnapshot encoded)
                    (Ok terminal)
                    "Terminal revision remains readable"

                let source = Encoding.UTF8.GetString encoded

                for revision in
                    [ "0"; "-1"; "9007199254740992"; "1.0"; "1e0"; "\"1\""; "null"; "true" ] do
                    let altered =
                        source.Replace("\"revision\":9007199254740991", "\"revision\":" + revision)
                        |> utf8

                    Expect.notEqual
                        altered
                        encoded
                        "Negative control actually changes bytes"

                    Expect.isError
                        (CaseRecord.decodeSnapshot altered)
                        "Accepted revisions are positive exact safe integers")
            testCase
                "retained case text is separate from current assertion policy and permanent Unicode"
                (fun () ->
                    let original = snapshot ()
                    let fields = viewFields original

                    let oldFields =
                        { fields with
                            Registration =
                                Inputs.registrationStateWith
                                    [
                                        BusinessField.ClaimantName,
                                        Admitted.Name(CaseFixtures.name "A\u202eB")
                                    ]
                                    fields.Registration
                        }

                    let retained =
                        { original with
                            State = CaseState.Active oldFields
                        }

                    let encoded = CaseRecord.encodeSnapshot retained

                    Expect.equal
                        (CaseRecord.decodeSnapshot encoded)
                        (Ok retained)
                        "Retained text is not readmitted"

                    for escaped in [ "\\u0000"; "\\ufdd0"; "\\ud800"; "\\udfff" ] do
                        let source = Encoding.UTF8.GetString encoded
                        let altered = source.Replace("A\u202eB", "A" + escaped + "B") |> utf8

                        Expect.notEqual
                            altered
                            encoded
                            "Unicode control changes the actual stored name"

                        Expect.isError
                            (CaseRecord.decodeSnapshot altered)
                            "Permanent invalid scalar grammar refuses")
            testCase
                "snapshot parsing and canonical bytes do not consult ambient culture"
                (fun () ->
                    let original = CultureInfo.CurrentCulture
                    let originalUi = CultureInfo.CurrentUICulture
                    let expected = snapshot ()
                    let encoded = CaseRecord.encodeSnapshot expected

                    try
                        for name in [ "en-US"; "lv-LV"; "ar-EG"; "tr-TR" ] do
                            CultureInfo.CurrentCulture <- CultureInfo.GetCultureInfo name
                            CultureInfo.CurrentUICulture <- CultureInfo.GetCultureInfo name

                            Expect.sequenceEqual
                                (CaseRecord.encodeSnapshot expected)
                                encoded
                                "Culture-independent canonical bytes"

                            Expect.equal
                                (CaseRecord.decodeSnapshot encoded)
                                (Ok expected)
                                "Culture-independent typed values"
                    finally
                        CultureInfo.CurrentCulture <- original
                        CultureInfo.CurrentUICulture <- originalUi)
            testCase
                "snapshots reject payload version tags and the previous financial collection shape"
                (fun () ->
                    let encoded =
                        snapshot () |> CaseRecord.encodeSnapshot |> Encoding.UTF8.GetString

                    Expect.equal InstallationFormat.Current 2 "Fresh installation grammar"

                    for altered in
                        [
                            encoded.Insert(1, "\"format\":2,")
                            encoded.Replace("\"finance\":", "\"payments\":[],\"finance\":")
                        ] do
                        Expect.isError
                            (CaseRecord.decodeSnapshot (utf8 altered))
                            "No payload alias or compatibility reader")
        ]
