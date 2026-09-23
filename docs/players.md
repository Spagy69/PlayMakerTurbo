# PlayMaker Turbo for players

PlayMaker Turbo makes My Winter Car run faster. Most of the game's logic runs on PlayMaker, and Turbo makes that
part cheaper without changing what the game does. The addon in this zip also removes the short freeze when you
first get into a car.

How much you gain depends on your computer and on where you are in the world. On the test computer, at one spot
in the house with no other mods, the game went from 72 to 125 FPS, and to 134 FPS with the mouse pick cache
switched on.

> **Beta.** It has been tested on one computer. It can still crash the game or break some game logic, and in the
> worst case damage your save. Back up your save first and keep the backup.

## What you need

- My Winter Car with MSCLoader installed.
- The game closed while you install.

## Back up your save

Copy this folder somewhere outside the game, for example to your desktop:

```
%USERPROFILE%\AppData\LocalLow\Amistech\My Winter Car
```

You can paste that line into the address bar of File Explorer.

## Install

1. Unpack the whole zip into one folder and keep the files together.
2. Run `PlayMakerTurbo Installer.exe` and press Install. It finds the game through Steam. If it does not, press
   Browse and pick the folder with `mywintercar.exe`.
3. Copy `Addon\PlayMakerTurboAddon.dll` into the game's `Mods` folder.
4. Start the game.

After every game update, and after Steam verifies the game files, run the installer again.

## Settings

In the game, open Mods, then PlayMaker Turbo Addon, then Settings. The upper part switches the addon's fixes,
the lower part Turbo's options. Changes apply after you restart the game.

With the defaults, the game logic works the same as in the original game, and the addon keeps the suspension of
parked cars within 0.1 mm of the original. One option is off by default: "Share mouse pick raycast in a frame".
It gave another 9 FPS in testing and is worth switching on. The
[settings page on GitHub](https://github.com/Spagy69/PlayMakerTurbo/blob/main/docs/settings.md) explains every
option.

## If something breaks

If a door does not open, an item cannot be picked up or a car part does not react, turn the options off one by
one in the settings, restarting the game each time, until it works again. Then tell us which option it was in
[Issues](https://github.com/Spagy69/PlayMakerTurbo/issues).

If the game does not start at all, run the installer and press Restore original game.

Uninstalling does not touch your save. If a save got damaged, copy your backup back in its place.

## Uninstall

Run the installer and press Restore original game, then delete `PlayMakerTurboAddon.dll` from the `Mods` folder.

## More

How it works and what was measured is on [GitHub](https://github.com/Spagy69/PlayMakerTurbo). The code was
written by an AI, directed and tested by the author; the page there explains how.

This is an unofficial fan project, not connected to Amistech Games, Hutong Games or the MSCLoader developers.
Do not send a patched `PlayMaker.dll` to anyone; share this zip instead. You use it at your own risk, see
`LICENSE`.
