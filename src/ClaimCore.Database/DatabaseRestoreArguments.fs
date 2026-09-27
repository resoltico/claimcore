namespace ClaimCore.Database

module internal DatabaseRestoreArguments =
    let private exactNonce (value: string) =
        value.Length = 64
        && value
           |> Seq.forall (fun character ->
               ('0' <= character && character <= '9') || ('a' <= character && character <= 'f'))

    let private paths
        report
        reportSignature
        evidenceIndex
        fence
        fenceSignature
        supplement
        supplementSignature
        =
        {
            Report = report
            ReportSignature = reportSignature
            EvidenceIndex = evidenceIndex
            Fence = fence
            FenceSignature = fenceSignature
            Supplement = supplement
            SupplementSignature = supplementSignature
        }

    let private parseRecheck =
        function
        | [ "verify-restore-report"; report; signature; evidenceIndex; nonce ] ->
            if exactNonce nonce then
                Some(
                    Ok(DatabaseCommand.VerifyRestoreReport(report, signature, evidenceIndex, nonce))
                )
            else
                Some(Error DatabaseInputProblem.UnsupportedInvocation)
        | [ "verify-fenced-tail"
            report
            reportSignature
            evidenceIndex
            fence
            fenceSignature
            supplement
            supplementSignature
            nonce ] ->
            if exactNonce nonce then
                Some(
                    Ok(
                        DatabaseCommand.VerifyFencedTail(
                            paths
                                report
                                reportSignature
                                evidenceIndex
                                fence
                                fenceSignature
                                supplement
                                supplementSignature,
                            nonce
                        )
                    )
                )
            else
                Some(Error DatabaseInputProblem.UnsupportedInvocation)
        | _ -> None

    let private parseActivation =
        function
        | [ "activate-writer-handoff"
            report
            reportSignature
            evidenceIndex
            fence
            fenceSignature
            supplement
            supplementSignature ] ->
            Some(
                Ok(
                    DatabaseCommand.ActivateWriterHandoff(
                        paths
                            report
                            reportSignature
                            evidenceIndex
                            fence
                            fenceSignature
                            supplement
                            supplementSignature
                    )
                )
            )
        | _ -> None

    let parse arguments =
        match parseRecheck arguments with
        | Some result -> Some result
        | None -> parseActivation arguments
