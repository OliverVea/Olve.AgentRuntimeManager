import "@typespec/http";

using Http;

namespace Arm;

/**
 * A skill registered in ARM for agents (M10b): a folder with a `SKILL.md` (the open Agent Skills
 * format: YAML frontmatter with `name` and `description`, then the instructions) and whatever
 * files it brings along, installed from a git repository. A `default` one goes to every session
 * that leaves `skills` unset; the others only to sessions that name them.
 *
 * A session takes copies of its skills' folders when its agent first starts: installing, replacing
 * or removing a skill later doesn't touch sessions that exist. The agent reaches them through
 * `arm_skill_load` (one of ARM's tools, `GET /api/tools`), whose description lists the session's
 * skills, so it never has to ask which it has.
 */
model Skill {
  /** What sessions name in `skills` (the registry's name, not necessarily the frontmatter's). */
  name: string;

  /** The frontmatter's `description`. */
  description: string;

  /** Whether every session that leaves `skills` unset gets it. */
  `default`: boolean;

  source: SkillSource;

  updatedAt: utcDateTime;
}

/** Where a skill comes from: a folder in a git repository. */
model SkillSource {
  /** The repository's clone URL (SSH or HTTPS). */
  @minLength(1) url: string;

  /** A branch, tag or commit. Unset: the repository's default branch. */
  @minLength(1) ref?: string;

  /** The skill's folder in the repository, holding its `SKILL.md`, e.g. `skills/pdf`. Unset: the root. */
  @minLength(1) path?: string;
}

model InstallSkill {
  source: SkillSource;

  /** Whether every session that leaves `skills` unset gets it. False by default. */
  `default`?: boolean;
}

model SkillName {
  @path @pattern("^[a-z0-9][a-z0-9-]*$") name: string;
}

@route("/skills")
interface Skills {
  /** Every registered skill, by name. */
  @get list(): Skill[] | UnauthorizedResponse;

  @get get(...SkillName): Skill | NotFound | UnauthorizedResponse;

  /**
   * Installs a skill, or replaces it: fetches the source and reads its `SKILL.md`. A source that
   * can't be fetched is a 400 `SKILL_SOURCE_FAILED` with git's error; a folder without a readable
   * `SKILL.md` is a 400 `INVALID_SKILL`.
   */
  @put install(...SkillName, @body body: InstallSkill): Skill | BadRequest | UnauthorizedResponse;

  @delete delete(...SkillName): NoContentResponse | NotFound | UnauthorizedResponse;
}
