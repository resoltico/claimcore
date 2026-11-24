import { Fragment, type ReactNode } from "react";

export const IdentifierValue = ({ value }: { value: string }) => {
  const parts: ReactNode[] = [];
  let offset = 0;
  for (const separator of value.matchAll(/[-/._:@]+/gu)) {
    parts.push(
      <Fragment key={separator.index}>
        {value.slice(offset, separator.index)}
        {separator[0]}
        <wbr />
      </Fragment>,
    );
    offset = separator.index + separator[0].length;
  }
  return (
    <bdi className="identifier-value" dir="ltr">
      {parts}
      {value.slice(offset)}
    </bdi>
  );
};
