const timeUnit = (seconds: number): readonly [Intl.RelativeTimeFormatUnit, number] => {
  if (seconds >= 86400) {
    return ["day", 86400];
  }
  if (seconds >= 3600) {
    return ["hour", 3600];
  }
  return ["minute", 60];
};
export const changedTime = (value: string, locale: string, now = Date.now()) => {
  const timestamp = new Date(value);
  const seconds = (timestamp.getTime() - now) / 1000;
  const [unit, divisor] = timeUnit(Math.abs(seconds));
  return {
    absolute: new Intl.DateTimeFormat(locale, { dateStyle: "long", timeStyle: "long" }).format(
      timestamp,
    ),
    relative: new Intl.RelativeTimeFormat(locale, { numeric: "auto" }).format(
      Math.round(seconds / divisor),
      unit,
    ),
  };
};
