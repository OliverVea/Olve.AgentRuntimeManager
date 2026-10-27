import { describe, expect, it } from "vitest";
import { defaultToolList } from "../testing/fake-api.js";
import { defaultTools, toolsField } from "./tool-picks.js";

const DEFAULTS = ["arm_bash", "arm_file_read", "arm_file_edit", "arm_file_create"];

describe("toolsField", () => {
  it("leaves the field out for the server's defaults, picked in any order", () => {
    expect(defaultTools(defaultToolList)).toEqual(DEFAULTS);
    expect(toolsField(defaultToolList, new Set(DEFAULTS))).toBeUndefined();
    expect(toolsField(defaultToolList, new Set([...DEFAULTS].reverse()))).toBeUndefined();
  });

  it("is [] when nothing is picked: no tools at all", () => {
    expect(toolsField(defaultToolList, new Set())).toEqual([]);
  });

  it("is the picked names, in the list's order, when the pick differs from the defaults", () => {
    expect(toolsField(defaultToolList, new Set(["arm_file_delete", "arm_bash"]))).toEqual([
      "arm_bash",
      "arm_file_delete",
    ]);
    expect(toolsField(defaultToolList, new Set([...DEFAULTS, "arm_file_delete"]))).toEqual([
      ...DEFAULTS,
      "arm_file_delete",
    ]);
    expect(toolsField(defaultToolList, new Set(["arm_bash"]))).toEqual(["arm_bash"]);
  });

  it("leaves the field out while there's no list, or when nothing is picked of a list with no defaults", () => {
    expect(toolsField(undefined, new Set())).toBeUndefined();
    const noDefaults = defaultToolList.map((t) => ({ ...t, default: false }));
    expect(toolsField(noDefaults, new Set())).toBeUndefined();
    expect(toolsField(noDefaults, new Set(["arm_bash"]))).toEqual(["arm_bash"]);
    expect(toolsField([], new Set())).toBeUndefined();
  });

  it("ignores picks the list doesn't have", () => {
    expect(toolsField(defaultToolList, new Set([...DEFAULTS, "gone"]))).toBeUndefined();
  });
});
