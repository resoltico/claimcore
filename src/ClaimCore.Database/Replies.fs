namespace ClaimCore.Database

open ClaimCore.Contract
open ClaimCore.Contract.Protocol

open System
open ClaimCore.Json
open ClaimCore.Application
open ClaimCore.Postgres

module Replies =
    let private encode members =
        JsonValue.Object members |> WireWriter.bytes

    let private text value = Codec.text.Write value
    let private number value = JsonValue.Integer value
    let outcome value exit = encode [ "kind", text value ], exit
    let private position value = ResponseCodecs.position.Write value

    let initialized id =
        encode [ "kind", text "INITIALIZED"; "installationId", Codec.uuid.Write id ], 0

    let head value =
        encode [ "kind", text "HEAD"; "position", position value ], 0

    let private accepted outcomeKind (receipt: OperationReceipt) =
        match receipt.Subject with
        | ReceiptSubject.Authority target ->
            encode
                [
                    "kind", text outcomeKind
                    "operationId", Codec.uuid.Write receipt.OperationId
                    "targetActorId", Codec.uuid.Write target.TargetActorId
                    "position", position receipt.Position
                ],
            0
        | _ -> outcome "FAILED" 3

    let authority =
        function
        | ProvisionOwnerOutcome.Accepted receipt -> accepted "ACCEPTED" receipt
        | ProvisionOwnerOutcome.AlreadyAccepted receipt -> accepted "ALREADY_ACCEPTED" receipt
        | ProvisionOwnerOutcome.Rejected _ -> outcome "REJECTED" 2
        | ProvisionOwnerOutcome.Failed _ -> outcome "FAILED" 3
        | ProvisionOwnerOutcome.Cancelled -> outcome "CANCELLED" 3
        | ProvisionOwnerOutcome.OutcomeUnknown operationId ->
            encode [ "kind", text "OUTCOME_UNKNOWN"; "operationId", Codec.uuid.Write operationId ],
            4

    let verification =
        function
        | VerificationResult.Verified value ->
            encode [ "kind", text "VERIFIED"; "position", position value ], 0
        | VerificationResult.Diverged finding ->
            encode
                [
                    "kind", text "DIVERGED"
                    "sequence",
                    (finding.Sequence |> Option.map number |> Option.defaultValue JsonValue.Null)
                    "finding", text (VerificationFindings.token finding.Finding)
                ],
            5

    let help () =
        encode
            [
                "exitCodes",
                JsonValue.Object
                    [
                        "success", number 0L
                        "rejected", number 2L
                        "failed", number 3L
                        "outcomeUnknown", number 4L
                        "diverged", number 5L
                    ]
                "acts",
                JsonValue.Array(
                    List.map
                        text
                        [ "initialize"; "provision-owner"; "verify"; "head"; "help"; "version" ]
                )
            ],
        0

    let version () =
        let identity = BuildIdentity.current

        encode
            [
                "product", text identity.Product
                "version", text identity.Version
                "assemblyVersion", text identity.AssemblyVersion
                "fileVersion", text identity.FileVersion
            ],
        0

    let write (output: IO.Stream) (bytes: byte array, code, deliveryUnknown) =
        try
            output.Write bytes
            output.WriteByte(byte '\n')
            output.Flush()
            code
        with _ ->
            if deliveryUnknown then 4 else 3
