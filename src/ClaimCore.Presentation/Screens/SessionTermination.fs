namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

module SessionTerminationScreen =
    let terminated (value: SessionTermination) =
        { HumanDocument.empty HumanDisclosure.Current with
            Screen =
                Screen.create
                    [
                        ScreenEntries.region
                            RegionRole.Notice
                            None
                            (RegionContent.Notice
                                [
                                    {
                                        MessageId = "label.sessionsTerminated"
                                        Reason = None
                                        Tone = NoticeTone.Info
                                    }
                                ])
                        ScreenEntries.entryRegion
                            RegionRole.Details
                            None
                            [
                                ScreenEntries.value
                                    "count"
                                    "label.browserSessionsEnded"
                                    Prominence.Primary
                                    (PresentedValue.Count value.Count)
                            ]
                    ]
        }

    let private notice messageId tone =
        { HumanDocument.empty HumanDisclosure.Current with
            Screen =
                Screen.create
                    [
                        ScreenEntries.region
                            RegionRole.Notice
                            None
                            (RegionContent.Notice
                                [
                                    {
                                        MessageId = messageId
                                        Reason = None
                                        Tone = tone
                                    }
                                ])
                    ]
        }

    let terminationUnknown () =
        notice "label.sessionTerminationUnknown" NoticeTone.Warning

    let retirementConfirmed () =
        notice "label.sessionRetirementConfirmed" NoticeTone.Info
