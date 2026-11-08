import type { Region, Screen } from "@contracts/client";
import { RegionView } from "./regions/RegionView";
import type { RegionInteraction } from "./regions/interaction";
import "./regions/region.css";
export const ScreenView = ({
  view,
  timeZone = "UTC",
  roles,
  ...interaction
}: RegionInteraction & {
  view: Screen;
  timeZone?: string;
  roles?: ReadonlyArray<Region["role"]>;
}) => (
  <div className="screen-view">
    {view.regions
      .filter((region) => roles === undefined || roles.includes(region.role))
      .map((region, index) => (
        <RegionView
          // oxlint-disable-next-line react/no-array-index-key -- FACTS regions can repeat, and regions own no input state.
          key={`${region.role}:${index}`}
          region={region}
          timeZone={timeZone}
          {...interaction}
        />
      ))}
  </div>
);
