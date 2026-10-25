import { describe, expect, it } from "vitest";
import { defaultModels, fakeApi } from "../testing/fake-api.js";
import { modelLabel } from "./format.js";
import { ModelCatalog, modelChoice } from "./model-catalog.js";

describe("modelChoice", () => {
  it("lists every provider's models by display names, the saved default selected", () => {
    expect(modelChoice(defaultModels, "claude/claude-sonnet-5")).toEqual({
      options: [
        { value: "claude/claude-opus-5-5", label: "Opus 5.5 (Claude Code)" },
        { value: "claude/claude-sonnet-5", label: "Sonnet 5 (Claude Code)" },
        { value: "fake/fake", label: "Instant (fake)" },
      ],
      selected: "claude/claude-sonnet-5",
    });
  });

  it("selects the first model when the saved default isn't offered (an old claude/sonnet)", () => {
    expect(modelChoice(defaultModels, "claude/sonnet").selected).toBe("claude/claude-opus-5-5");
    expect(modelChoice(defaultModels, "").selected).toBe("claude/claude-opus-5-5");
  });

  it("holds only the saved default, by its ids, while there's no list", () => {
    expect(modelChoice(undefined, "claude/sonnet")).toEqual({
      options: [{ value: "claude/sonnet", label: "sonnet (claude)" }],
      selected: "claude/sonnet",
    });
    expect(modelChoice(undefined, "")).toEqual({ options: [], selected: "" });
  });

  it("selects nothing when the server offers no models", () => {
    expect(modelChoice([], "claude/sonnet")).toEqual({ options: [], selected: "" });
  });
});

describe("modelLabel", () => {
  it("names a model by its display names when the list has it, else by its ids", () => {
    const opus = { provider: "claude", model: "claude-opus-5-5" };
    expect(modelLabel(opus, defaultModels)).toBe("Opus 5.5 (Claude Code)");
    expect(modelLabel(opus)).toBe("claude-opus-5-5 (claude)");
    expect(modelLabel({ provider: "claude", model: "sonnet" }, defaultModels)).toBe(
      "sonnet (claude)",
    );
  });
});

describe("ModelCatalog", () => {
  it("reads the providers and their models, and forgets them when cleared", async () => {
    const catalog = new ModelCatalog(fakeApi().client);
    let changes = 0;
    catalog.addEventListener("change", () => changes++);
    await catalog.load();
    expect(catalog.loaded).toBe(true);
    expect(catalog.providers).toEqual(defaultModels);
    expect(catalog.problem).toBe("");

    catalog.clear();
    expect(catalog.loaded).toBe(false);
    expect(catalog.providers).toEqual([]);
    expect(changes).toBe(2);
  });

  it("keeps why a read failed", async () => {
    const catalog = new ModelCatalog(fakeApi({ models: 500 }).client);
    await catalog.load();
    expect(catalog.loaded).toBe(false);
    expect(catalog.problem).toBe("The server failed.");
  });
});
