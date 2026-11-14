import type { Link, RegionRow, Affordance } from "@contracts/client";
import type { ReactNode } from "react";

export type RowLink = {
  href: string;
  follow: () => void;
  kind?: "ACTOR" | "REFERENCE";
  column?: string;
  label?: string;
  id?: string;
};
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
