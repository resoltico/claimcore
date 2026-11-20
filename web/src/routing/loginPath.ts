import { presentationPseudolanguage } from "@contracts/client";
import type { Language } from "../presentation/preferences";

export const loginPath = (language: Language): string =>
  language === presentationPseudolanguage ? "/auth/login" : `/auth/login?language=${language}`;
