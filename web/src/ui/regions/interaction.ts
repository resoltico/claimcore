import type { ReactNode } from "react";
import type { RegionContent } from "@contracts/client";
import type { RowLink } from "./Table";
export type OfferedAct = Extract<
  RegionContent,
  { kind: "ACTIONS" }
>["groups"][number]["acts"][number];
export type RegionInteraction = {
  renderActions?: (key: string) => ReactNode;
  rowLink?: (key: string) => RowLink | undefined;
  sectionLink?: RowLink;
  countLink?: RowLink;
  onAct?: (act: OfferedAct) => void;
  labelledBy?: string;
  headerActions?: ReactNode;
};
