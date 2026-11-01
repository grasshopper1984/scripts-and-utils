# Repository instructions

This repository is public. Follow these rules for every change.

## Local development

- Make changes, build, and test on a local-only branch, such as `local/<topic>`.
- Keep development branches local: do not push them or create remote branches
  for pull requests. Do not configure an upstream for a development branch.
- Reserve `main` for reviewed, tested changes prepared for publication.

## Review before integration

- Review the complete proposed change, including new files, generated output,
  documentation, filenames, and commit messages, for information that should
  not become public.
- Run a credential/secret scan over all files proposed for publication and
  manually review for personal information. Automated scanning alone is not
  sufficient. Resolve findings before integrating into `main`.
- Check for passwords, tokens, API keys, private keys, connection strings,
  credential-bearing URLs, private email addresses, personal records, local
  user paths, private hostnames, and identifying logs or screenshots.
- Use placeholders or synthetic data for examples. Keep real credentials,
  personal configuration, local logs, and sensitive artifacts out of Git.
- Check author and committer metadata before committing to `main`; use an
  intended public identity and do not expose a private email address.
- Complete the checks relevant to the change on the local development branch.
  Record what was checked and any limitations; do not claim unrun tests passed.

## Squash into main

- Fetch `origin/main` before integration and reconcile any upstream changes on
  the local development branch. Repeat affected checks there when necessary.
- Integrate each completed change into `main` as a single squash commit using
  `git merge --squash <local-branch>` followed by a reviewed commit.
- Do not merge development history with a merge commit, fast-forward it into
  `main`, or replay individual development commits onto `main`.
- Inspect the staged squash result before committing. Check every outgoing
  commit, its files, message, and metadata before pushing; a clean final diff
  does not guarantee earlier outgoing commits are safe to publish.
- If information needs removal, fix it locally before publication. Do not rely
  on a later public deletion to undo a leak.

## Publication

- The only permitted pushed ref is `refs/heads/main`, sourced from local
  `refs/heads/main`. Never push local development branches, tags, or other refs.
- Use an explicit push such as
  `git push origin refs/heads/main:refs/heads/main`.
- Do not use `--all`, `--tags`, `--mirror`, or bypass the local push guard with
  `--no-verify`. Do not force-push or rewrite published history without an
  explicit instruction from the user.
- The same publication rules apply to GitHub APIs and other tools; do not use
  them to bypass the local workflow or push guard.
- Return to a local-only branch for subsequent development, builds, and tests.

## Local enforcement

This checkout has `push.default=nothing`, `push.followTags=false`, and a local
`.git/hooks/pre-push` guard that permits only `main` to `main` pushes. These Git
settings and the hook are local and are not carried into other clones. The
hook checks refs only; squash discipline and the credential/personal-data
review above remain required.
