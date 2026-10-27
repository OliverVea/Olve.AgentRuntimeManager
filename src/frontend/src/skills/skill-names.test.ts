import { describe, expect, it } from "vitest";
import { isValidSkillName, skillNameProblem } from "./skill-names.js";

describe("skill names", () => {
  it("are lowercase letters, digits and -, not starting with -", () => {
    for (const name of ["pdf", "arm-conventions", "9lives", "a", "x-1-y"]) {
      expect(isValidSkillName(name), name).toBe(true);
      expect(skillNameProblem(name), name).toBeUndefined();
    }
    for (const name of ["", "-pdf", "PDF", "my skill", "pipelines_", "a/b", "ünïcode"]) {
      expect(isValidSkillName(name), name).toBe(false);
      expect(skillNameProblem(name), name).toBe(
        "Names are lowercase letters, digits and -, not starting with -.",
      );
    }
  });
});
