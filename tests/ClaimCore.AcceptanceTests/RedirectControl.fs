module internal ClaimCore.AcceptanceTests.RedirectControl

open System.IO
open System.Text.Json
open System.Text.Json.Nodes

[<NoEquality; NoComparison>]
type Armed =
    {
        Id: string
        Path: string
        Status: int
    }

type Control(directory: string) =
    let gate = obj ()
    let mutable current: Armed option = None
    let mutable consumed = false
    let mutable originals = 0
    let mutable destinations = 0
    let mutable reply: JsonNode | null = null

    let persist () =
        let node = JsonObject()
        node["originals"] <- JsonValue.Create originals
        node["destinations"] <- JsonValue.Create destinations
        node["reply"] <- reply

        FixtureFiles.text directory "redirect-evidence.json" (node.ToJsonString())
        |> ignore

        node.Remove "reply" |> ignore

    member _.Arm(path) =
        lock gate (fun () ->
            let file = Path.Combine(directory, "redirect-control.json")

            if File.Exists file then
                use document = JsonDocument.Parse(File.ReadAllBytes file)
                let root = document.RootElement
                let id = root.GetProperty("id").GetString() |> nonNull

                if current |> Option.forall (fun value -> value.Id <> id) then
                    current <-
                        Some
                            {
                                Id = id
                                Path = root.GetProperty("path").GetString() |> nonNull
                                Status = root.GetProperty("status").GetInt32()
                            }

                    consumed <- false
                    originals <- 0
                    destinations <- 0
                    reply <- null

            match current with
            | Some value when value.Path = path && not consumed ->
                consumed <- true
                Some value.Status
            | _ -> None)

    member _.Original(bytes: byte array option) =
        lock gate (fun () ->
            originals <- originals + 1

            reply <-
                bytes
                |> Option.map (fun value -> JsonNode.Parse(value))
                |> Option.defaultValue null

            persist ())

    member _.Destination() =
        lock gate (fun () ->
            destinations <- destinations + 1
            persist ())
