﻿namespace MyGame
open System
open Prime
open Nu

// this module provides global handles to the game's key simulants.
// having a Simulants module for your game is optional, but can be nice to avoid duplicating string literals across
// the code base.
[<RequireQualifiedAccess>]
module Simulants =

    // splash screen
    let Splash = Screen "Splash"

    // title screen
    let Title = Screen "Title"
    let TitleGui = Title / "Gui"
    let TitleGuiPlay = TitleGui / "Play"
    let TitleGuiCredits = TitleGui / "Credits"
    let TitleGuiExit = TitleGui / "Exit"

    // credits screen
    let Credits = Screen "Credits"
    let CreditsGui = Credits / "Gui"
    let CreditsGuiBack = CreditsGui / "Back"

    // gameplay screen
    let Gameplay = Screen "Gameplay"
    let GameplayGui = Gameplay / "Gui"
    let GameplayGuiQuit = GameplayGui / "Quit"
    let GameplayScene = Gameplay / "Scene"
    let GameplayScenePlayer = GameplayScene / "Player"