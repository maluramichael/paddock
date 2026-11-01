# Contributing to Paddock

Thanks for taking the time to contribute! This guide covers the scope, the build setup, the quality bar and the commit conventions that drive automatic releases.

## Before you build a feature

Paddock has a deliberate scope: **organize the Windows desktop with translucent areas without getting in the way and without wasting resources.** That means:

- areas never steal focus, never cover normal windows and never poll in the background;
- Explorer behaviour (context menu, drag and drop, undo) stays native instead of being re-implemented;
- new options need a good reason, because sensible defaults beat settings.

If your idea does not obviously fit, please [open a feature request](https://github.com/maluramichael/paddock/issues/new/choose) and ask before building it. Many ideas fit better as a [plugin](docs/PLUGINS.md) or a [theme](docs/THEMES.md).

## Build setup

You need Windows 10 or 11 and the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0). Any editor works: Visual Studio 2022, Rider or VS Code with C# Dev Kit.

```powershell
git clone https://github.com/maluramichael/paddock.git
cd paddock
dotnet build Paddock.sln
dotnet test tests/Paddock.Tests
```

**Always test with a throwaway profile** so you never touch your own areas:

```powershell
$env:PADDOCK_DATA = "$env:TEMP\paddock-dev"
dotnet run --project src/Paddock
```

Only one Paddock instance can run at a time. Quit your installed copy from the tray first.

A full release build (tests, self-contained publish, example plugin, MSI and portable ZIP) is a single command:

```powershell
./build/build.ps1 -Version 1.2.3     # results in ./artifacts
```

The MSI is built with [WiX 5](https://wixtoolset.org/), which is restored from NuGet automatically, so there is nothing to install.

## Code guidelines

- **No new compiler warnings.** CI builds with the same settings.
- **Event-driven only.** No timers that poll. Use `FileSystemWatcher`, window messages or WinEvents, and debounce with a one-shot `DispatcherTimer` if needed.
- **Never block the UI thread** with I/O longer than a few milliseconds. Icons load on the STA worker in `ShellIcons`.
- **Keep native interop in `src/Paddock/Interop`**, with a short comment on *why* a Win32 call is needed.
- **Small methods.** Split instead of nesting conditions.
- **User-facing text is German** for now. Keep it short and friendly and use "du".
- Add or extend a test in `tests/Paddock.Tests` for logic that does not need a window (snapping, rules, paths …).

## Commit messages and versioning

Releases are fully automatic. Every push to `main` builds the MSI and publishes a GitHub release. The next version is derived from the commit messages since the last tag, so please use [Conventional Commits](https://www.conventionalcommits.org/):

| Prefix | Example | Version bump |
|--------|---------|--------------|
| `fix:` | `fix: collapsed fences no longer jump after DPI change` | patch `1.4.2 → 1.4.3` |
| `feat:` | `feat: rule condition for file size` | minor `1.4.2 → 1.5.0` |
| `docs:`, `chore:`, `refactor:`, `perf:`, `test:`, `ci:` | `perf: cache display names` | patch |
| `feat!:` or a `BREAKING CHANGE:` footer | `feat!: new config format` | **major** `1.4.2 → 2.0.0` |

Major versions are meant to be rare. Use them only for changes that break existing configurations, themes or plugins, and discuss them in an issue first. Changes that only touch Markdown files or `docs/` do not trigger a release. To skip a release for another commit, add `[skip release]` to its message.

Maintainers can also run the **Release** workflow by hand (Actions → Release → *Run workflow*) and choose `patch`, `minor` or `major` explicitly.

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Keep the PR focused: one feature or fix per PR.
3. Make sure `dotnet build` shows no new warnings and `dotnet test` passes.
4. For UI changes, add before/after screenshots.
5. Fill in the pull request template.

PRs are squash-merged, so the **PR title** becomes the commit message on `main` and decides the version bump. Please give it a Conventional Commits prefix.

## License of contributions

By contributing you agree that your contribution is licensed under the [project license](LICENSE) (MIT with Attribution).
