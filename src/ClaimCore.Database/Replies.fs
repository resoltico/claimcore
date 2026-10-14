namespace ClaimCore.Database

open System
open System.Text.Json
open ClaimCore.Application

module Replies =
    let encode value =
        JsonSerializer.SerializeToUtf8Bytes value

    let tag value exit = encode {| tag = value |}, exit

    let position (value: LedgerPosition) =
        {|
            sequence = value.Sequence.ToString(Globalization.CultureInfo.InvariantCulture)
            hash = value.EntryHash
        |}

    let authority =
        function
        | AuthorityOutcome.Applied(event, head, actor) ->
            encode
                {|
                    tag = "APPLIED"
                    eventId = event
                    actorId = actor
                    position = position head
                |},
            0
        | AuthorityOutcome.AlreadyApplied(event, head, actor) ->
            encode
                {|
                    tag = "ALREADY_APPLIED"
                    eventId = event
                    actorId = actor
                    position = position head
                |},
            0
        | AuthorityOutcome.Rejected _ -> tag "REFUSED" 2
        | AuthorityOutcome.Failed _ -> tag "FAILED" 3
        | AuthorityOutcome.Cancelled -> tag "CANCELLED" 3
        | AuthorityOutcome.OutcomeUnknown event ->
            encode
                {|
                    tag = "OUTCOME_UNKNOWN"
                    eventId = event
                |},
            4

    let help () =
        encode
            {|
                commands =
                    [|
                        "initialize"
                        "migrate"
                        "provision-owner"
                        "verify"
                        "head"
                        "help"
                        "version"
                    |]
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
