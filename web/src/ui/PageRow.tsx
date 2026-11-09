import type { PresentedPage } from "../protocol/types";
import { SectionsView } from "./SectionsView";

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
  for (const { view: screen, basis } of views) {
    for (const region of screen.regions) {
      const view = region.content;
      if (view.kind !== "TABLE") {
        continue;
      }
      const row = view.rows[remaining];
      if (row !== undefined && remaining >= 0) {
        return (
          <SectionsView
            timeZone={basis.timeZone}
            sections={
              row.detail?.sections ?? [
                {
                  titleId,
                  entries: view.columns.flatMap((column, position) => {
                    const value = row.values[position];
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
              ]
            }
          />
        );
      }
      remaining -= view.rows.length;
    }
  }
  return null;
};
