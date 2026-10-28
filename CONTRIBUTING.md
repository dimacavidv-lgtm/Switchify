# Contributing to Switchify

Thanks for your interest! Changes reach `main` only through pull requests that the maintainer reviews and merges.

## How to propose a change

1. **Fork** the repository and create a branch for your change.
2. Make the change and **build it** to check that it compiles and works:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\build.ps1 .\Switchify-test.exe
   ```
   With an output path, `build.ps1` only compiles (it does not touch an installed copy). No SDK is needed: it uses the C# compiler that ships with Windows.
3. Open a **pull request** describing what it changes and how you tested it. Screenshots or a short recording help for anything visual.

For bigger changes, open an issue first so we can agree on the approach.

## Guidelines

- Keep Switchify **dependency-free**: only what Windows and .NET Framework 4 include.
- Follow the style of the surrounding code. Code comments are in Spanish.
- Write commit messages in **English**.
- Never overwrite the user's original files: results go next to them.
- Do not commit compiled files (`*.exe`, `*.pdb`).

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
