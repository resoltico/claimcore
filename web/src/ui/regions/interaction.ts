import type { Link, RegionRow, Affordance } from "@contracts/client";
import type { ReactNode } from "react";

import type { RowLink } from "./Table";
export type LinkNavigation = (link: Link) => Pick<RowLink, "id" | "follow"> | undefined;
export type ActionPresentation = (action: Affordance) => { label?: string; id?: string };
export type RegionInteraction = {
  actionPresentation?: ActionPresentation;
  linkNavigation?: LinkNavigation;
  actionsDisabled?: boolean;
  onAction?: (action: Affordance, row?: RegionRow, originId?: string) => void;
  sectionLink?: RowLink;
  countLink?: RowLink;
  labelledBy?: string;
  headerActions?: ReactNode;
};
