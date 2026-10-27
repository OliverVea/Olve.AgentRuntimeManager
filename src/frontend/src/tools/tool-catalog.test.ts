import { describe, expect, it } from "vitest";
import { defaultToolList, fakeApi } from "../testing/fake-api.js";
import { ToolCatalog } from "./tool-catalog.js";

describe("ToolCatalog", () => {
  it("reads ARM's tools, and forgets them when cleared", async () => {
    const catalog = new ToolCatalog(fakeApi().client);
    let changes = 0;
    catalog.addEventListener("change", () => changes++);
    await catalog.load();
    expect(catalog.loaded).toBe(true);
    expect(catalog.tools).toEqual(defaultToolList);
    expect(catalog.problem).toBe("");

    catalog.clear();
    expect(catalog.loaded).toBe(false);
    expect(catalog.tools).toEqual([]);
    expect(changes).toBe(2);
  });

  it("keeps why a read failed", async () => {
    const catalog = new ToolCatalog(fakeApi({ tools: 500 }).client);
    await catalog.load();
    expect(catalog.loaded).toBe(false);
    expect(catalog.problem).toBe("The server failed.");
  });
});
