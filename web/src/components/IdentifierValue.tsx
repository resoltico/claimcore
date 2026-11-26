import { breakAfter } from "./ValueBreaks";

export const IdentifierValue = ({ value }: { value: string }) => (
  <bdi className="identifier-value" dir="ltr">
    {value.length <= 16 ? (
      <span className="short-identifier">{value}</span>
    ) : (
      breakAfter(value, /[-/._:@]+/gu)
    )}
  </bdi>
);
