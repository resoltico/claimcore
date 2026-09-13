namespace ClaimCore.Web

open ClaimCore.Contracts

module WebRecoveryWire =
    let list value =
        WebWireCodec.recoveryList value |> EncodedJson.ok

    let inspect value =
        WebWireCodec.recoveryInspect value |> EncodedJson.ok

    let resolve endpoint value =
        WebWireCodec.resolve endpoint value |> EncodedJson.ok

    let dismiss value =
        WebWireCodec.recoveryDismiss value |> EncodedJson.ok

    let export value =
        WebWireCodec.recoveryExport value |> EncodedJson.ok

    let importQuery endpoint value =
        WebWireCodec.importPreview endpoint value |> EncodedJson.ok

    let importRetain endpoint value =
        WebWireCodec.importRetain endpoint value |> EncodedJson.ok
