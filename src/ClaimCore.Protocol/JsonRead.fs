namespace ClaimCore.Protocol

open System.Text.Json

module internal JsonRead =
    let objectValue allowed (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            JsonInput.reject "INVALID_SHAPE"

        for property in element.EnumerateObject() do
            if not (List.contains property.Name allowed) then
                JsonInput.reject "UNKNOWN_PROPERTY"

    let required name read (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            JsonInput.reject "INVALID_SHAPE"

        match element.TryGetProperty(name: string) with
        | true, value -> read value
        | false, _ -> JsonInput.reject "MISSING_PROPERTY"

    let optional name read (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            JsonInput.reject "INVALID_SHAPE"

        match element.TryGetProperty(name: string) with
        | true, value -> Some(read value)
        | false, _ -> None

    let nullable read (element: JsonElement) =
        if element.ValueKind = JsonValueKind.Null then
            None
        else
            Some(read element)

    let items minimum maximum (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Array then
            JsonInput.reject "INVALID_SHAPE"

        let count = element.GetArrayLength()

        if minimum |> Option.exists (fun limit -> count < limit) then
            JsonInput.reject "INVALID_VALUE"

        if maximum |> Option.exists (fun limit -> count > limit) then
            JsonInput.reject "INVALID_VALUE"

        element.EnumerateArray() |> Seq.toArray

    let array minimum maximum read element =
        items minimum maximum element |> Array.map read |> Array.toList

    let dictionary read (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            JsonInput.reject "INVALID_SHAPE"

        element.EnumerateObject()
        |> Seq.map (fun p -> p.Name, read p.Value)
        |> Map.ofSeq

    let tag name element = required name ScalarRead.text element

    let singleton expected actual =
        if not (JsonElement.DeepEquals(expected, actual)) then
            JsonInput.reject "INVALID_VALUE"
