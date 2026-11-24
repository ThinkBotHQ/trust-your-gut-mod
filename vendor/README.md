# vendor

`bepinex-5.4.23.6/` holds the Unity Doorstop 4.6.0 loader files that `mod/install.sh` copies into the game folder:

| File | What it is |
|---|---|
| `winhttp.dll` | Unity Doorstop 4.6.0 (Windows x64). Wine loads it in place of its own winhttp. |
| `.doorstop_version` | Doorstop version marker. |
| `doorstop_config.ini` | Template. `install.sh` writes a copy that points at `TygMod\TygMod.dll`. |
| `changelog.txt` | Changelog from the same zip. |
| `LICENSE-LGPL-2.1.txt` | The full text of the GNU Lesser General Public License 2.1. |

Source: `BepInEx_win_x64_5.4.23.6.zip` from the BepInEx release v5.4.23.6
(https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.6). BepInEx itself is not used (it cannot run on this game).

## License

Unity Doorstop (copyright NeighTools and the Unity Doorstop contributors) and BepInEx (copyright the BepInEx
contributors) are licensed under the GNU Lesser General Public License 2.1 (LGPL-2.1).
The full license text is in `bepinex-5.4.23.6/LICENSE-LGPL-2.1.txt`. These files are not changed.
Under the LGPL you may replace `winhttp.dll` with your own build of Unity Doorstop.

The source code of this Doorstop version is at https://github.com/NeighTools/UnityDoorstop/tree/v4.6.0
(project: https://github.com/NeighTools/UnityDoorstop). BepInEx source: https://github.com/BepInEx/BepInEx/tree/v5.4.23.6.
If these links stop working, open an issue in this repository and we will send you the source code of this Doorstop version.

The MIT license in the top-level `LICENSE` file covers only this project's own code. It does not cover the files in this folder.
