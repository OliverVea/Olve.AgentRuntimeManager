import { describe, expect, it } from "vitest";
import { defaultSkillList } from "../testing/fake-api.js";
import { defaultSkills, skillsField } from "./skill-picks.js";

const DEFAULTS = ["arm-conventions", "pipelines"];

describe("skillsField", () => {
  it("leaves the field out for the registered defaults, picked in any order", () => {
    expect(defaultSkills(defaultSkillList)).toEqual(DEFAULTS);
    expect(skillsField(defaultSkillList, new Set(DEFAULTS))).toBeUndefined();
    expect(skillsField(defaultSkillList, new Set([...DEFAULTS].reverse()))).toBeUndefined();
  });

  it("is [] when nothing is picked: no skills", () => {
    expect(skillsField(defaultSkillList, new Set())).toEqual([]);
  });

  it("is the picked names, in the list's order, when the pick differs from the defaults", () => {
    expect(skillsField(defaultSkillList, new Set(["pipelines", "pdf"]))).toEqual([
      "pdf",
      "pipelines",
    ]);
    expect(skillsField(defaultSkillList, new Set([...DEFAULTS, "pdf"]))).toEqual([
      "arm-conventions",
      "pdf",
      "pipelines",
    ]);
    expect(skillsField(defaultSkillList, new Set(["pipelines"]))).toEqual(["pipelines"]);
  });

  it("leaves the field out while there's no list, or when nothing is picked of a list with no defaults", () => {
    expect(skillsField(undefined, new Set())).toBeUndefined();
    const noDefaults = defaultSkillList.map((s) => ({ ...s, default: false }));
    expect(skillsField(noDefaults, new Set())).toBeUndefined();
    expect(skillsField(noDefaults, new Set(["pdf"]))).toEqual(["pdf"]);
    expect(skillsField([], new Set())).toBeUndefined();
  });

  it("ignores picks the list doesn't have", () => {
    expect(skillsField(defaultSkillList, new Set([...DEFAULTS, "gone"]))).toBeUndefined();
  });
});
