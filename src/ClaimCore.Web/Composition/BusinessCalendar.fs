namespace ClaimCore.Web.Composition

open System
open ClaimCore.Application

module internal BusinessCalendar =
    let resolve value =
        let zone = TimeZoneInfo.FindSystemTimeZoneById value

        { new IBusinessCalendar with
            member _.TimeZoneId = zone.Id

            member _.DateAt instant =
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime)
        }
