module ClaimCore.Tests.FlatGroupCodecTests

open System
open System.Text
open Expecto
open ClaimCore.Json

let private pairCodec () =
    let first = Codec.record id |> Codec.field "name" id Codec.text
    let second = Codec.record id |> Codec.field "count" id (Codec.integer 0L 10L)

    (Codec.record (fun name count -> name, count): Fields<string * int64, _>)
    |> Codec.flatten fst first
    |> Codec.flatten snd second
    |> Codec.build

[<Tests>]
let tests =
    testList
        "flat coherent codec groups"
        [
            testCase "groups retain independent fields in one wire object" (fun () ->
                let codec = pairCodec ()
                let expected = Encoding.UTF8.GetBytes "{\"name\":\"sample\",\"count\":3}"

                Expect.equal
                    (Codec.encode codec ("sample", 3L))
                    expected
                    "The flat wire shape is independently authored"

                Expect.equal
                    (Codec.decode 1000 codec expected)
                    (Ok("sample", 3L))
                    "Both coherent values decode")
            testCase "missing and unadvertised members are rejected across groups" (fun () ->
                let codec = pairCodec ()

                for raw in
                    [
                        "{\"name\":\"sample\"}"
                        "{\"count\":3}"
                        "{\"name\":\"sample\",\"count\":3,\"extra\":true}"
                    ] do
                    Expect.isError
                        (Codec.decode 1000 codec (Encoding.UTF8.GetBytes raw))
                        "The composed object remains closed and complete")
            testCase "overlapping group members are rejected at declaration" (fun () ->
                let first = Codec.record id |> Codec.field "same" id Codec.text
                let second = Codec.record id |> Codec.field "same" id Codec.text

                Expect.throwsT<ArgumentException>
                    (fun () ->
                        (Codec.record (fun first second -> first, second)
                        : Fields<string * string, _>)
                        |> Codec.flatten fst first
                        |> Codec.flatten snd second
                        |> Codec.build
                        |> ignore)
                    "Two owners cannot define one wire member")
        ]
