# Skip-CI tokens break builds on `main`

**Read this before writing any commit message, squash message or PR merge in this repo.**

## The rule

GitHub does not start `push` or `pull_request` workflows when the commit message
contains any of these strings, anywhere in the subject **or the body**:

| Token | Effect |
|-------|--------|
| `[skip ci]` | no CI, no CodeQL, no Increment Build Version, so no CD |
| `[ci skip]` | same |
| `[no ci]` | same |
| `[skip actions]` | same |
| `[actions skip]` | same |

A `skip-checks: true` trailer (two blank lines then `skip-checks: true` at the end
of the message) is related: it suppresses check runs. Don't use it either.

For a push, GitHub looks at the commit message being pushed. For a squash merge
into `main` that is the **squash message**. The repo's squash default
(`COMMIT_MESSAGES`) builds that message from **every commit message on the PR
branch**, so one token on any branch commit (even quoted inside a sentence in a
body) ends up in `main`'s merge commit, and `main` gets no push CI at all.

## What it broke

- **#155 and #162 landed with no CI on `main`.** Their squash messages carried
  the token from the ApplicationVersion bot's commits (`ci: set ApplicationVersion
  to N for merge to main` plus the token). No main build, no main tests, no
  CodeQL and no build artifacts for those commits.
- **CD for 3.6.0-b261 failed.** Bump Display Version passed CD the
  "newest successful push CI on main" as `ci_run_id`. With main CI skipped, that
  run was weeks old, its `build-*` artifacts had expired (5-day retention) and CD
  stopped with `Artifact not found for name: build-windows`. If the artifacts had
  still been live it would have been worse: CD would have shipped stale source
  under the new version. (#166 makes CD build from `package_sha` and guards
  `ci_run_id`, but main still loses its CI signal whenever the token lands.)
- **PR CI can vanish too.** When the head commit of a PR push carries the token,
  even only quoted in its body, GitHub skips every workflow for that push. That
  happened to `7a5d666` on #166.

## What to do

1. **Squash merges: delete every token line from the squash message before
   confirming.** In the GitHub UI, edit the "Extended description" box. With `gh`,
   pass an explicit body:
   `gh pr merge <N> --squash --subject "<PR title> (#<N>)" --body "<summary without any token>"`.
   Check the subject too, and don't prefix a product merge's subject with `ci:`
   (see [agent-playbook-merge-client-pr-v2.md](agent-playbook-merge-client-pr-v2.md)).
2. **Never write the literal token in a commit message**, not even to explain
   it. Write "skip-ci token" or similar. A quote triggers it exactly like real use.
   (Docs and code files are fine: GitHub only reads commit messages.)
3. **Bots don't need it.** The ApplicationVersion on PR workflow
   (`.github/workflows/bump-version-on-pr.yml`) pushes with `GITHUB_TOKEN`, and
   GitHub never starts workflows (push or `pull_request` synchronize) from a
   `GITHUB_TOKEN` push. So the in-flight PR CI is never cancelled or restarted,
   and the bot's message carries no token. Keep it that way: a token in a bot
   commit on a PR branch reaches `main` through the squash message. The main-only
   version commits (`ci: bump …` from Bump Display Version, `ci: auto-increment …`)
   are skipped by `ci.yml`'s subject guards on those prefixes, not by a token.
4. **Recommended repo setting (Jon's call, not a code change):** Settings →
   General → Pull Requests → Allow squash merging → default commit message
   **"Pull request title and description"** (`squash_merge_commit_message=PR_BODY`).
   The squash body then comes from the PR description, not from branch commits,
   so a stray token on a branch can't reach `main`. Until that is set, rule 1 applies
   to every merge.

## Checking

```bash
# Any token in the commits a PR would squash? (empty output = safe)
git log --format='%H %B' origin/main..HEAD | grep -inE '\[(skip ci|ci skip|no ci|skip actions|actions skip)\]|skip-checks: *true'

# Did push CI run for the merge commit on main?
gh run list --branch main --event push --commit <merge-sha> --workflow "CI - Build & Test"
```

If push CI did not run for a merge, `main` has no artifacts for it. Don't pass
any old `ci_run_id` to CD. Dispatch CD without one (it builds from `package_sha`).
