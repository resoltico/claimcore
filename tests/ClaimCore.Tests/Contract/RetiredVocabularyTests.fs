module ClaimCore.Tests.RetiredVocabularyTests

open Expecto
open ClaimCore.Protocol
open ClaimCore.Json

[<Tests>]
let tests =
    testList
        "retired unproduced vocabulary"
        [
            testCase "unproduced actions are no longer admitted" (fun () ->
                let codec = VocabularyCodec.create RecommendedAction.vocabulary

                for token in [ "REPLAY_EXACT"; "REAUTHENTICATE" ] do
                    let bytes = System.Text.Encoding.UTF8.GetBytes("\"" + token + "\"")

                    Expect.isError
                        (Codec.decode 100 codec bytes)
                        "Removed action spellings cannot be authored")
            testCase "unproduced session-forbidden host failure is refused" (fun () ->
                Expect.isError
                    (HostFailureCodecs.decode
                        "{\"hostFailure\":{\"code\":\"SESSION_FORBIDDEN\",\"phase\":\"NOT_STARTED\"}}"B)
                    "The boundary no longer advertises an unreachable host result"

                Expect.isOk
                    (HostFailureCodecs.decode
                        "{\"hostFailure\":{\"code\":\"SESSION_REJECTED\",\"phase\":\"NOT_STARTED\"}}"B)
                    "The produced credential rejection remains admitted")
        ]
