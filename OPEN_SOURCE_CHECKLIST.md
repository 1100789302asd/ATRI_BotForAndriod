# Open Source Checklist

Use this before making the repository public.

## Secrets

- Run a secret scan before publishing:

```powershell
rg -n --hidden --glob '!Library/**' --glob '!Temp/**' --glob '!Logs/**' --glob '!.git/**' -i "(api[_-]?key|secret|token|password|authorization|bearer|cookie|client_secret|private_key|\\.env)"
```

- Verify sensitive files are not tracked:

```powershell
git ls-files | Select-String -Pattern "(cookie|credential|secret|token|password|\\.env|keystore|\\.jks|\\.p12|\\.pfx|\\.pem|\\.key)"
```

- If a real secret was ever committed, remove it from Git history and rotate it.
  Deleting it in the latest commit is not enough. GitHub's guide is here:
  https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/removing-sensitive-data-from-a-repository

## Licensing

- Choose a license for original code. This repository currently uses MIT.
- Keep third-party notices intact.
- Do not claim third-party assets are MIT licensed unless you own them or have
  written permission.
- Add a visible disclaimer that this is an unofficial fan/learning project if
  you keep ATRI-themed material.

## Assets

- Decide whether the public repository is code-only or includes assets.
- Remove or replace assets with unclear redistribution rights:
  `Assets/model/atri`, `Assets/StreamingAssets/model/atri`,
  `Assets/Resources/bk`, music/audio files, voice files, and custom fonts.
- Consider Git LFS or Release attachments for large files that you are allowed
  to redistribute.

## Unity Hygiene

- Keep `Library/`, `Temp/`, `Logs/`, `UserSettings/`, generated `.csproj`, and
  generated `.sln` files out of Git.
- Keep `.meta` files for tracked Unity assets.
- Do not track `.meta` files for ignored local-only files such as cookies.

## Final Review

- Open the project once in Unity after cleanup.
- Confirm the default scene loads.
- Confirm local-only configs are recreated or documented.
- Review `git status --short` before the first public push.
