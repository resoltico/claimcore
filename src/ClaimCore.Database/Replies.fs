namespace ClaimCore.Database

open ClaimCore.Contract

open System
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Postgres

module Replies =
    let encode value =
        JsonSerializer.SerializeToUtf8Bytes value

    let tag value exit = encode {| tag = value |}, exit

    let position (value: LedgerPosition) =
        {|
            sequence = value.Sequence
            hash = value.EntryHash
        |}

    let private accepted outcomeTag (receipt: OperationReceipt) =
        match receipt.Subject with
        | ReceiptSubject.Authority target ->
            encode
                {|
                    tag = outcomeTag
                    operationId = receipt.OperationId
                    targetActorId = target.TargetActorId
                    position = position receipt.Position
                |},
            0
        | _ -> tag "FAILED" 3

    let authority =
        function
        | ProvisionOwnerOutcome.Accepted receipt -> accepted "ACCEPTED" receipt
        | ProvisionOwnerOutcome.AlreadyAccepted receipt -> accepted "ALREADY_ACCEPTED" receipt
        | ProvisionOwnerOutcome.Rejected _ -> tag "REFUSED" 2
        | ProvisionOwnerOutcome.Failed _ -> tag "FAILED" 3
        | ProvisionOwnerOutcome.Cancelled -> tag "CANCELLED" 3
        | ProvisionOwnerOutcome.OutcomeUnknown operationId ->
            encode
                {|
                    tag = "OUTCOME_UNKNOWN"
                    operationId = operationId
                |},
            4

    let verification =
        function
        | VerificationResult.Verified head ->
            encode
                {|
                    tag = "VERIFIED"
                    position = position head
                |},
            0
        | VerificationResult.Diverged finding ->
            encode
                {|
                    tag = "DIVERGED"
                    sequence = finding.Sequence
                    finding = VerificationFindings.token finding.Finding
                |},
            5

    let help () =
        encode
            {|
                exitCodes =
                    {|
                        success = 0
                        refused = 2
                        failed = 3
                        outcomeUnknown = 4
                        diverged = 5
                    |}
                acts = [| "initialize"; "provision-owner"; "verify"; "head"; "help"; "version" |]
            |},
        0

    let version () = encode BuildIdentity.current, 0

    let write (output: IO.Stream) (bytes: byte array, code, deliveryUnknown) =
        try
            output.Write bytes
            output.WriteByte(byte '\n')
            output.Flush()
            code
        with _ ->
            if deliveryUnknown then 4 else 3
