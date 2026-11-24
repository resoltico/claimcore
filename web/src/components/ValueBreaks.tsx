import { Fragment, type ReactNode } from "react";

export const breakAfter = (value: string, separators: RegExp): ReactNode[] => {
  const parts: ReactNode[] = [];
  let offset = 0;
  for (const separator of value.matchAll(separators)) {
    parts.push(
      <Fragment key={separator.index}>
        {value.slice(offset, separator.index)}
        {separator[0]}
        <wbr />
      </Fragment>,
    );
    offset = separator.index + separator[0].length;
  }
  parts.push(value.slice(offset));
  return parts;
};
