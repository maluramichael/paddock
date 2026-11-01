# Summary

<!-- What does this change do, in one or two sentences? -->

## Motivation

<!-- Why is this change needed? Link any related issue, e.g. "Closes #12". -->

## Testing

- [ ] `dotnet build Paddock.sln` is clean (no new warnings)
- [ ] `dotnet test tests/Paddock.Tests` passes
- [ ] Tried it with a throwaway profile (`$env:PADDOCK_DATA`) on Windows 10 / 11

<!-- Note anything you could not test and why (multi-monitor, high DPI, light theme …). -->

## Screenshots

<!-- For UI changes, add before/after screenshots. -->

## Checklist

- [ ] PR title follows Conventional Commits (`fix:`, `feat:`, `feat!:` …). It decides the next version number.
- [ ] No polling or timers added. New work is event-driven.
- [ ] New user-facing text is German, short and consistent with the rest of the UI
- [ ] Docs updated (`README.md`, `docs/THEMES.md`, `docs/PLUGINS.md`) if behaviour changed
- [ ] Base branch is `main`
