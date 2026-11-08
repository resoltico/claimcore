import type { PresentedPage } from "../protocol/types";
import { RecordView } from "./RecordView";

export const PageRow = ({
  views,
  index,
  titleId,
}: {
  views: ReadonlyArray<PresentedPage>;
  index: number;
  titleId: string;
}) => {
  let remaining = index;
  for (const { view, basis } of views) {
    if (view.kind !== "TABLE") {
      continue;
    }
    const row = view.rows[remaining];
    if (row !== undefined && remaining >= 0) {
      return (
        <RecordView
          timeZone={basis.timeZone}
          view={{
            kind: "RECORD",
            sections: view.details[remaining]?.sections ?? [
              {
                titleId,
                entries: view.columns.flatMap((column, position) => {
                  const value = row[position];
                  return value === undefined
                    ? []
                    : [
                        {
                          key: column.key,
                          labelId: column.labelId,
                          prominence: column.prominence,
                          value,
                        },
                      ];
                }),
              },
            ],
          }}
        />
      );
    }
    remaining -= view.rows.length;
  }
  return null;
};
