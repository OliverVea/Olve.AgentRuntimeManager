import { describe, expect, it } from "vitest";
import { checkRepoRows, isBlankRepoRow } from "./repo-rows.js";

const ARM = "git@github.com:OliverVea/Olve.AgentRuntimeManager.git";
const TRAINS = "git@github.com:OliverVea/Olve.Trains.git";

describe("checkRepoRows", () => {
  it("makes the session's repos: trimmed, an empty ref left out, blank rows ignored", () => {
    expect(
      checkRepoRows([
        { url: ` ${ARM} `, ref: "  " },
        { url: "", ref: " " },
        { url: TRAINS, ref: " main " },
      ]),
    ).toEqual({
      problems: [undefined, undefined, undefined],
      repos: [{ url: ARM }, { url: TRAINS, ref: "main" }],
    });
    expect(checkRepoRows([])).toEqual({ problems: [], repos: [] });
  });

  it("flags each bad row on its field, with the mock's words, and gives no repos", () => {
    const check = checkRepoRows([
      { url: "--upload-pack=evil", ref: "" },
      { url: ARM, ref: "my branch" },
      { url: "", ref: "main" },
      { url: TRAINS, ref: "" },
      { url: "https://github.com/OliverVea/Olve.Trains", ref: "4c1d9e7" },
      { url: "git@github.com:OliverVea/Olve.Utilities.git", ref: "-b" },
      { url: "https://host/.git", ref: "" },
    ]);
    expect(check.repos).toBeUndefined();
    expect(check.problems).toEqual([
      { part: "url", text: "A clone URL can't start with -." },
      { part: "ref", text: "A ref is a branch, tag or commit: no spaces, not starting with -." },
      { part: "url", text: "Give this ref a clone URL." },
      undefined,
      { part: "url", text: "Another repo above would also go in the folder Olve.Trains." },
      { part: "ref", text: "A ref is a branch, tag or commit: no spaces, not starting with -." },
      { part: "url", text: "This URL has no repository name to name its folder after." },
    ]);
  });

  // The server's rules (RepoErrors.Check, RepoNames.Folder) use .NET's whitespace and control
  // characters: the composer refuses what the server refuses, and lets through what it accepts.
  it.each([
    ["a tab", "a\tb"],
    ["a no-break space", "a b"],
    ["a next-line control", "a\u0085b"],
    ["a C1 control", "a\u009bb"],
    ["an ideographic space", "a　b"],
  ])("refuses a ref with %s, as the server does", (_, ref) => {
    expect(checkRepoRows([{ url: ARM, ref }]).problems[0]?.part).toBe("ref");
  });

  it("lets through a ref the server accepts (no .NET whitespace or control in it)", () => {
    expect(checkRepoRows([{ url: ARM, ref: "release/1.0﻿" }]).repos).toEqual([
      { url: ARM, ref: "release/1.0﻿" },
    ]);
  });

  it("finds two URLs that land in one folder however they're written", () => {
    expect(
      checkRepoRows([
        { url: "git@github.com:a/tools.git", ref: "" },
        { url: "https://github.com/b/tools/", ref: "" },
      ]).problems[1],
    ).toEqual({ part: "url", text: "Another repo above would also go in the folder tools." });
    expect(
      checkRepoRows([
        { url: "/srv/git/tools.GIT", ref: "" },
        { url: "git@host:Tools", ref: "" },
      ]).repos,
    ).toHaveLength(2); // folders are case-sensitive, as on the server
  });

  it("a row is blank with nothing but spaces in it", () => {
    expect(isBlankRepoRow({ url: " ", ref: "　" })).toBe(true);
    expect(isBlankRepoRow({ url: "", ref: "main" })).toBe(false);
  });
});
