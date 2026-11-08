namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module OperationScreen =
    let pending finalAt =
        Screen.create
            [
                region
                    RegionRole.Notice
                    None
                    (RegionContent.Notice
                        {
                            MessageId = "label.operationPending"
                            Reason = Some(PresentedValue.Instant finalAt)
                            Tone = NoticeTone.Info
                        })
            ]

    let notAccepted () =
        Screen.create
            [
                region
                    RegionRole.Notice
                    None
                    (RegionContent.Notice
                        {
                            MessageId = "label.operationNotAccepted"
                            Reason = None
                            Tone = NoticeTone.Final
                        })
            ]
