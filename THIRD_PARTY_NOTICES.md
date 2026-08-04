# Third-Party Notices

This repository contains original code plus third-party packages and assets.
The project license does not override any third-party license.

## Code Dependencies

- Unity and Unity packages are governed by Unity's own terms.
- Live2D Cubism SDK for Unity is governed by the licenses included under `Assets/Live2D/Cubism`. Business use may require a separate Cubism SDK Release License: https://www.live2d.com/en/download/cubism-sdk/release-license/
- Live2D's bundled sample models have separate Free Material License terms. See `Assets/Live2D/Cubism/LICENSE.md` and Live2D's model terms: https://www.live2d.com/eula/live2d-sample-model-terms_en.html
- SocketIOUnity `1.1.5` is MIT licensed. See `Assets/SocketIOUnity-1.1.5/LICENSE`.
- TextMesh Pro sample resources and LiberationSans are included with their own notices under `Assets/TextMesh Pro`.

## Assets Requiring Verification

Before publishing this repository publicly, confirm that you have explicit
permission to redistribute these files, or remove/replace them:

- ATRI character/model resources under `Assets/model/atri` and `Assets/StreamingAssets/model/atri`.
- Voice, music, and audio files under `Assets/model/atri` and `Assets/StreamingAssets/model/atri`.
- Background videos under `Assets/Resources/bk`.
- The font file `Assets/Font/STXINWEI.TTF`.
- Imported Unity package archives such as `Assets/CubismSdkForUnity-*.unitypackage`.

If permission is unclear, publish a code-only repository and provide setup
instructions telling users where to place their own licensed assets.

## Services

- The NetEase Cloud Music integration stores login cookies locally in
  `netease_cookie.json`. Do not commit this file.
- Any public API base URL in config files is a default endpoint, not a secret.
  Users should review the endpoint terms and replace it with their own service
  if needed.
