# PlayMaker Turbo for players

PlayMaker Turbo makes My Winter Car run faster. Most of the game's logic runs on PlayMaker, and Turbo makes
that part cheaper without changing what the game does. The addon in this zip also removes the freeze when you
first get into a car. How much you gain depends on your computer and where you are in the world. On the test
computer the `fpstest` command of the Better FPS mod went from 88 to 108 FPS with version 1.1, and to 117 FPS
with the mouse pick cache switched on.

> **Beta.** It has been tested on one computer. It can still crash the game or break some game logic, and in the
> worst case damage your save. Back up your save first (see below) and keep the backup.

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

1. Unpack the whole zip into one folder. Keep all the files together.
2. Run `PlayMakerTurbo Installer.exe` and press Install. It finds the game through Steam. If it does not, press
   Browse and pick the folder with `mywintercar.exe`.
3. Copy `Addon\PlayMakerTurboAddon.dll` into the game's `Mods` folder.
4. Start the game.

Run the installer again after every game update, and after Steam verifies the game files.

## Settings

In the game, open Mods, then PlayMaker Turbo Addon, then Settings. The top part turns the addon's fixes on and
off, the part below turns Turbo's options on and off. Turbo's defaults give the same game logic as the original,
and the addon's suspension IK stays within 0.1 mm of it. "Share mouse pick raycast in a frame" is off by default; switching it on gave another 8 FPS in testing. Changes
apply after you restart the game.

## If something breaks

A door that does not open, an item you cannot pick up, a car part that does not react: turn the options off one
by one in the settings above, restarting the game each time, until it works again. Then tell us which option it
was in [Issues](https://github.com/Spagy69/PlayMakerTurbo/issues).

If the game does not start at all, run the installer and press Restore original game.

Uninstalling does not touch your save. If a save got damaged, copy your backup back in its place.

## Uninstall

Run the installer and press Restore original game, then delete `PlayMakerTurboAddon.dll` from the `Mods` folder.

## More

Everything else, including how it works and what was measured, is on
[GitHub](https://github.com/Spagy69/PlayMakerTurbo). The code was written by an AI, directed and tested by the
author; the page there explains how.

This is an unofficial fan project, not connected to Amistech Games, Hutong Games or the MSCLoader developers.
Do not send a patched `PlayMaker.dll` to anyone; share this zip instead. You use it at your own risk; see
`LICENSE`.
