# Releasing

For the maintainer. Pushing a version tag runs the [Release workflow](.github/workflows/release.yml),
which tests and builds the Linux x64 and arm64 executables and publishes them as a GitHub release.

## Steps

1. Merge everything the release should include into `main`.
2. Pick the version (see [Versions](#versions)).
3. Tag the latest `main` and push the tag:

   ```
   git fetch origin
   git tag -a v0.2.0 -m "v0.2.0" origin/main
   git push origin v0.2.0
   ```

   - `origin/main` tags the commit that's on GitHub's `main`, whichever branch you have checked out and
     whether or not your local `main` is up to date.
   - `-a` makes an annotated tag, which records who made the release and when. A plain
     `git tag v0.2.0` also starts a release, but stores only the name.

4. Watch the run under **Actions → Release**. It takes about two minutes. The release then appears
   under **Releases** with `item-copy-linux-x64.tar.gz`, `item-copy-linux-arm64.tar.gz` and
   `SHA256SUMS`, and notes listing the pull requests merged since the previous release.
5. Install it with the [README command](README.md#install) and run `item-copy --version`. It prints
   the version, then `+` and the commit it was built from, e.g. `0.2.0+1108c32…`.

Don't create the release with GitHub's **Draft a new release** button. The workflow creates it, and
fails if the tag already has a release.

## Versions

- The tag sets the version: `v0.2.0` builds `0.2.0`. Leave `<Version>` in the csproj at `0.0.0-dev`, so
  local builds are obviously not releases.
- Use `MAJOR.MINOR.PATCH`: patch for fixes, minor for new features, major for changes that break
  existing job files or `.env` setups.
- Only tags shaped like `v1.2.3` start a release.
- A suffix, as in `v0.3.0-beta.1`, makes a pre-release. The install command uses `releases/latest`,
  which skips pre-releases.

## If the release build fails

No release is created and the tag stays. Fix the problem in a pull request, then tag the next patch
version (`v0.2.1`). Don't move or re-push a tag after pushing it.

## Trying a build without releasing

**Actions → Release → Run workflow**, on any branch. It runs the tests and builds both executables
(version `0.0.0-dev`) without creating a release, and attaches the archives to the run for 3 days.
