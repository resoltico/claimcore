import { createContext } from "react";
import type { LinkNavigation } from "../ui/regions/interaction";
export const LinkNavigationContext = createContext<LinkNavigation | undefined>(undefined);
