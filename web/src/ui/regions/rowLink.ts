import type { Link } from "@contracts/client";
import type { LinkNavigation, RowLink } from "./interaction";
import { coreLinkPath } from "../../routing/coreLinks";
import { navigate } from "../../routing/navigation";

/** All row variants preserve the same Core query and optional interface navigation callback. */
export const coreRowLink = (
  link: Link | undefined,
  navigation?: LinkNavigation,
): RowLink | undefined => {
  if (link === undefined) {
    return undefined;
  }
  const path = coreLinkPath(link);
  return path === undefined
    ? undefined
    : {
        href: path,
        follow: () => {
          navigate(path);
        },
        ...navigation?.(link),
      };
};
