import type { Region, Screen } from "@contracts/client";
import { RegionView } from "./regions/RegionView";
import type { RegionInteraction } from "./regions/interaction";
import "./regions/region.css";
export const ScreenView = ({
  view,
  timeZone = "UTC",
  roles,
  embedded = false,
  ...interaction
}: RegionInteraction & {
  view: Screen;
  timeZone?: string;
  roles?: ReadonlyArray<Region["role"]>;
  embedded?: boolean;
}) => (
  <div className="screen-view">
    {view.regions
      .filter((region) => roles === undefined || roles.includes(region.role))
      .map((region, index) => (
        <RegionView
          // oxlint-disable-next-line react/no-array-index-key -- FACTS regions can repeat, and regions own no input state.
          key={`${region.role}:${index}`}
          region={region}
          embedded={embedded}
          timeZone={timeZone}
          {...interaction}
        />
      ))}
  </div>
);
