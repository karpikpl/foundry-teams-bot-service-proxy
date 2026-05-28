# Contributing

Thanks for your interest!

This is a sample, so the bar is pragmatic: focus on bug fixes, doc clarity, and small targeted features. Larger architectural rewrites should start with an issue.

## Development setup

You need:

- .NET SDK **10.0** or newer
- Docker (for the image build)
- Optional: an AAD-authed env (`az login`) if you want to actually talk to Foundry while developing

```bash
git clone https://github.com/karpikpl/foundry-teams-bot-service-proxy.git
cd foundry-teams-bot-service-proxy

dotnet restore
dotnet test
```

## Code style

- Follow the existing patterns — there's an `.editorconfig` at the repo root
- Keep methods focused; extract helpers when a method passes ~80 lines
- Comment **why**, not **what** — the code says what; reserve comments for non-obvious tradeoffs
- Public methods get XML doc comments; private helpers don't unless they're tricky

## Tests

Every PR must:

1. Keep existing tests passing
2. Add tests for new behavior (look at the AdaptiveCardBuilderTests for the tree-walking style — assert behavior, not DOM structure)
3. Not increase the number of `// TODO` or `// HACK` comments

Run them with `dotnet test` from the repo root.

## Pull requests

- Small and focused — one logical change per PR
- Include a brief rationale (the bug, the feature, the decision)
- Link to any related issues
- CI must be green before review

## Releases (maintainers)

Tag with `vX.Y.Z` on `main`; the [release workflow](.github/workflows/release.yml) builds and pushes a multi-arch image to GHCR with cosign signing. Prerelease tags (`vX.Y.Z-rc.N`) get pushed but skip `latest`/floating tags.
