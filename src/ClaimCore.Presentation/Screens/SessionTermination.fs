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

    let terminationUnknown () =
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
                                        MessageId = "label.sessionTerminationUnknown"
                                        Reason = None
                                        Tone = NoticeTone.Warning
                                    }
                                ])
                    ]
        }
