# BlockHp

Harmony mod for 7 Days to Die that shows a colored X on damaged blocks near you

Tested on 3.0

## Problem

During a horde night or a long build session it's easy to lose track of what actually got damaged Zombies chip away at walls scaffolding gets shot up and you end up eyeballing every block trying to spot cracks A quick visual overlay saves the guesswork

## What it does

- Press J to toggle a damage overlay on and off
- Damaged blocks get a colored X drawn on the face pointing toward you
- Color goes from red to green based on how much HP the block has left
- Only checks blocks within a set radius around the player
- Updates live when a block takes damage or changes and also rescans periodically to catch anything missed
- Multiblock structures are handled correctly the X shows up on whichever exposed face is actually facing the camera
- Terrain air and child blocks are skipped since they don't have real HP to track

<img width="1082" height="981" alt="image" src="https://github.com/user-attachments/assets/d9086fe3-def2-422a-a153-689d78c129ec" />

## How it works

Hooks into the game's block damage and block change events instead of polling every block every frame Damaged positions get queued up and processed on the main thread so there's no threading weirdness

For each damaged block the mod checks all six neighboring faces and picks the one that's both open and facing the camera the most directly That face gets the X drawn on it using GL immediately after the camera renders so it draws in world space and gets occluded properly by anything solid in front of it

A periodic sphere scan around the player catches blocks that were already damaged before the overlay was turned on and drops markers that fall outside the radius as you move If a block briefly can't be read because its chunk isn't loaded the mod keeps the last known face cached so the marker doesn't flicker

<img width="1382" height="933" alt="image" src="https://github.com/user-attachments/assets/bcb1fea0-4d46-499c-838c-9960cf008cde" />

## Config

There's no external config file for this one The settings live as constants in the code

- ToggleKey is J by default
- Radius is 40 blocks around the player
- RescanInterval is how often it rescans in seconds default 3
- MaxMarkers caps how many markers can exist at once default 1500 farthest ones get dropped first

If you want different values you'll need to edit the source and rebuild

<img width="1055" height="970" alt="image" src="https://github.com/user-attachments/assets/743953ca-2ede-40cf-866c-182c3286f3d6" />

## Install

Drop the folder in Mods That's it

## Compatibility

Only patches EntityPlayerLocal.Awake as a postfix to attach a rendering component to the player's camera Everything else runs through normal game events and read only block queries so it doesn't touch any vanilla behavior This is a purely visual client side overlay so it should be safe to use on any server without needing it installed server side

Built against 3.0 internals Might break on other versions if these change
