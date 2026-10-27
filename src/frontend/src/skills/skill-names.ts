/**
 * The names skills may have, as the server checks them (`SkillName` in the contract): lowercase
 * letters, digits and `-`, not starting with `-`. Checked here too so a bad name shows on its
 * field before anything is sent.
 */

const validName = /^[a-z0-9][a-z0-9-]*$/;

export function isValidSkillName(name: string): boolean {
  return validName.test(name);
}

/** What's wrong with a name, if anything. */
export function skillNameProblem(name: string): string | undefined {
  if (!isValidSkillName(name))
    return "Names are lowercase letters, digits and -, not starting with -.";
  return undefined;
}
