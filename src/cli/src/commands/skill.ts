import { type Skill, type SkillSource, skillsDelete, skillsGet, skillsInstall, skillsList } from "@arm/client";
import { unwrap } from "../api";
import { text, urlRef } from "../options";
import { formatKeyValue, formatTable } from "../output";
import type { CommandGroup } from "../registry";

const nameArg = { name: "name", description: "The skill's name in ARM (what sessions name in --skill): lower-case letters, digits and dashes" };

/** A skill's source on one line: `url#ref path`, leaving out what isn't set. */
export function formatSource(source: SkillSource): string {
  const repo = source.ref ? `${source.url}#${source.ref}` : source.url;
  return source.path ? `${repo} ${source.path}` : repo;
}

/** One row per skill: its name, whether a session without `--skill` gets it, where it comes from and what it does. */
export function formatSkills(skills: readonly Skill[]): string {
  if (skills.length === 0) return "No skills installed.";
  return formatTable(skills, [
    { header: "skill", value: (s: Skill) => s.name },
    { header: "default", value: (s: Skill) => (s.default ? "yes" : "no") },
    { header: "source", value: (s: Skill) => formatSource(s.source) },
    { header: "description", value: (s: Skill) => s.description },
  ]);
}

/** Key/value lines for one skill; an unset ref or path is left out. */
export function formatSkill(s: Skill): string {
  const entries: Array<readonly [string, unknown]> = [
    ["name", s.name],
    ["description", s.description],
    ["default", s.default ? "yes" : "no"],
    ["url", s.source.url],
    ["ref", s.source.ref],
    ["path", s.source.path],
    ["updatedAt", s.updatedAt],
  ];
  return formatKeyValue(entries.filter(([, v]) => v !== undefined));
}

export const skillGroup: CommandGroup = {
  name: "skill",
  summary: "Skills installed for agents: folders with a SKILL.md, which sessions name to let the agent load them",
  defaultCommand: "list",
  commands: [
    {
      name: "list",
      summary: "Every installed skill",
      args: [],
      options: {},
      async run({ client }) {
        const skills = await unwrap(skillsList({ client }), client);
        return { json: skills, pretty: formatSkills(skills) };
      },
    },
    {
      name: "get",
      summary: "Show one skill: its description and where it was installed from",
      args: [nameArg],
      options: {},
      async run({ args, client }) {
        const skill = await unwrap(skillsGet({ client, path: { name: args.name! } }), client);
        return { json: skill, pretty: formatSkill(skill) };
      },
    },
    {
      name: "install",
      summary:
        "Install a skill, or replace it: a folder of a git repository holding a SKILL.md (popular Claude skills install as they are)",
      args: [
        nameArg,
        {
          name: "url",
          description: "The repository's clone URL (SSH or HTTPS); #REF takes a branch, tag or commit (default: the repository's default branch)",
        },
      ],
      options: {
        path: {
          type: "string",
          description: "The skill's folder in the repository, holding its SKILL.md, e.g. skills/pdf (default: the root)",
          valueName: "FOLDER",
        },
        default: { type: "boolean", description: "Give it to every session that names no skills (else only to sessions that --skill it)" },
      },
      async run({ args, options, client }) {
        const { url, ref } = urlRef(args.url!, "<url>");
        const path = text(options, "path");
        const skill = await unwrap(
          skillsInstall({
            client,
            path: { name: args.name! },
            body: { source: { url, ...(ref ? { ref } : {}), ...(path ? { path } : {}) }, default: options.default === true },
          }),
          client,
        );
        return {
          json: skill,
          pretty: `Installed ${skill.name}${skill.default ? " (every session that names no skills gets it)" : ""}.\n${skill.description}`,
        };
      },
    },
    {
      name: "remove",
      summary: "Remove a skill (sessions that took it keep their copy)",
      args: [nameArg],
      options: {},
      async run({ args, client }) {
        await unwrap(skillsDelete({ client, path: { name: args.name! } }), client);
        return { json: {}, pretty: `Removed ${args.name} (sessions that took it keep their copy).` };
      },
    },
  ],
};
