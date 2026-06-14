I'll preface this by being perfectly clear that ***THIS WASN'T CODED BY ME***, it was made with ***Claude AI***, i've only done tweaks and adjustments to some of the code, i'm not a developer, i'm a 3D artist, so i'll leave the source code of each version/update if people want to expand or take it over feel free to do so, i don't take credit for any of the code written nor the idea of the mod itself, i simply took the initiative to see if it was even possible, so here it is!

**What does the mod do and what features does it have :**

- This mod allows the player to hide specific HUD icons, divided by categories, types of units and factions. (Radar ping hud icons are still shown regardless of settings, as they automatically hide themselves after a few seconds)

- It *Should* be compatible with other unit mods, like NAVEX for example, i haven't tested every mods out there, but in theory it should, there's a system in place to read modded units and categorise them at runtime,  i left a fallback system for base game units and most commonly used mods like QOL, Aryx's mods etc.

- I made sure that the mod is optimised, i've used a profiler during testing (Both vanilla and modded) and it's stable at a cost of around 0.005ms, without spikes during gameplay.

- Should be compatible with future game updates, if i understand it correctly (I probably don't) as long as definitions and classes that are used for units don't change, it shouldn't break with updates.

- Toggles settings are retained between sessions. (Config Manager is required)

- It works online.

That's about it, i initially made this for myself but since i don't intend on maintaining this heavily for too long, i figured i might as well share it.
If you have suggestions let me know, if you're interested in taking over the mod feel free to do so as well.

https://github.com/Overshoot999/HUD-Icon-Toggle/releases
