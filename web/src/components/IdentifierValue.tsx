import { breakAfter } from "./ValueBreaks";

export const IdentifierValue = ({ value }: { value: string }) => (
  <bdi className="identifier-value" dir="ltr">
    {breakAfter(value, /[-/._:@]+/gu)}
  </bdi>
);
