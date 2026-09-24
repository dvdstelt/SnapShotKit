# Releasing SnapShotKit

Cutting a release is drafting it on GitHub. The version is derived from the tag by MinVer, so no version number is edited in a file, and the Release workflow builds and attaches every package.

## Steps

1. Add a `<release>` entry at the top of the `<releases>` block in `packaging/io.github.dvdstelt.SnapShotKit.metainfo.xml`, with the version and today's date. That is the version history GNOME Software shows. If the spec's `Version:` is behind, bring it up to the same version, since COPR builds from the spec rather than from a tag. Commit on a branch and merge it.
2. On GitHub, go to Releases and draft a new release. Create a new tag in the form `vX.Y.Z` (for example `v0.2.0`) targeting `main`, and write the release notes. **Save it as a draft; do not publish yet.**
3. Run the **Release** workflow manually, giving it that tag as `draft_tag`. It builds every package and attaches them to the draft.
4. Check the draft: for each of x86-64 and arm64 there is an RPM, a `.deb` and an AppImage, six files, and `sha256sums.txt` lists them all. Download one and run it.
5. Publish the draft. GitHub creates the tag at that moment, so nothing is ever pushed from a terminal. A tag containing `-` (for example `v0.3.0-beta.1`) should be marked as a pre-release.
6. Rebuild COPR from the new tag, so `dnf` users get it too:

   ```bash
   copr-cli buildscm --clone-url https://github.com/dvdstelt/SnapShotKit.git --commit vX.Y.Z --spec packaging/snapshotkit.spec snapshotkit
   ```

Publishing first and letting the workflow fill the release in afterwards also works, and is one step shorter. The reason the draft comes first is the few minutes in between: a published release with no downloads on it is worse than no release at all.

### Why the draft needs its tag passed in

GitHub does not create a git tag until a release is published, so while it is still a draft there is no tag for MinVer to read and the packages would come out as `0.1.1-alpha.0.N`. Passing `draft_tag` gives the build the version the release is about to have. Publishing the draft then creates that same tag on the same commit, and the two agree.

## Versioning

SnapShotKit follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html): breaking is major, a feature is minor, a fix is patch. Pre-release suffixes sort alphabetically, so use `-alpha`, `-beta` and then `-preview` as the last stage before a stable release.

MinVer derives the version from the nearest tag. A commit that is not tagged builds as a pre-release of the next patch, so a development build reports something like `0.1.1-alpha.0.7` and is never mistaken for a release.

Each package format spells a pre-release its own way, because neither dpkg nor rpm accepts a `-` in a version: `0.2.0-beta.1` becomes `0.2.0~beta.1` in the `.deb` and version `0.2.0`, release `0.beta.1` in the RPM. Both sort before `0.2.0` itself. The mapping is in `scripts/package/common.sh`.

## Rehearsing a release

Running the Release workflow with `draft_tag` left empty builds everything for both architectures and uploads nothing. The packages are kept as workflow artifacts, so they can be tried before anything is drafted.

Locally, on Fedora:

```bash
scripts/package/all.sh      # the .deb and the AppImage for this machine, into dist/
scripts/package/rpm.sh      # the RPM, which needs the spec's build requirements installed
```

## If the release workflow fails

A published release stays published with whatever it managed to attach. Either delete the release and its tag and start again, or fix the workflow and re-run the failed jobs so the missing packages are attached. For a draft, fix the problem and dispatch the workflow again: `--clobber` replaces whatever was attached before.
