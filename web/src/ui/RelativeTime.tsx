import { localeNumbers } from "../presentation/localeNumbers";
import { usePresentation } from "../presentation/context";
import { useRequesterContext } from "../hooks/useRequesterContext";
/** Both instants come from the read model, so a local clock error cannot change the displayed age. */
export const RelativeTime = ({ value, observedAt }: { value: string; observedAt: string }) => {
  const p = usePresentation();
  const requester = useRequesterContext();
  const seconds = (new Date(value).getTime() - new Date(observedAt).getTime()) / 1000;
  const magnitude = Math.abs(seconds);
  let unit: Intl.RelativeTimeFormatUnit = "second";
  let divisor = 1;
  if (magnitude >= 86400) {
    unit = "day";
    divisor = 86400;
  } else if (magnitude >= 3600) {
    unit = "hour";
    divisor = 3600;
  } else if (magnitude >= 60) {
    unit = "minute";
    divisor = 60;
  }
  const locale = new Intl.Locale(p.language === "en-XA" ? "en" : p.language, {
    numberingSystem: localeNumbers(p.displayLocale).numberingSystem,
  });
  const text = new Intl.RelativeTimeFormat(locale, { numeric: "auto" }).format(
    Math.round(seconds / divisor),
    unit,
  );
  return (
    <time
      dateTime={value}
      title={requester === null ? value : p.recordedTime(value, requester.timeZone)}
    >
      {text}
    </time>
  );
};
