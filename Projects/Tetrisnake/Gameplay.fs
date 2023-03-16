﻿namespace MyGame
open System
open Prime
open Nu
open Nu.Declarative
open MyGame

[<AutoOpen>]
module Gameplay =

    // this is our Elm-style model type representing gameplay.
    type Gameplay =
        | Playing
        | Quitting
        | Quit

    // this is our Elm-style message type.
    type GameplayMessage =
        | StartQutting
        | FinishQuitting
        interface Message

    // this is our Elm-style command type. Commands are used instead of messages when things like physics are involved.
    type GameplayCommand =
        | Update
        | PostUpdateEye
        | Jump
        | Nop
        interface Command

    // this extends the Screen API to expose the above Gameplay model.
    type Screen with
        member this.GetGameplay world = this.GetModelGeneric<Gameplay> world
        member this.SetGameplay value world = this.SetModelGeneric<Gameplay> value world
        member this.Gameplay = this.ModelGeneric<Gameplay> ()

    // this is the screen dispatcher that defines the screen where gameplay takes place
    type GameplayDispatcher () =
        inherit ScreenDispatcher<Gameplay, GameplayMessage, GameplayCommand> (Quit)

        // here we define the screen's properties and event handling
        override this.Initialize (_, _) =
            [Screen.UpdateEvent => Update
             Screen.PostUpdateEvent => PostUpdateEye
             Screen.DeselectingEvent => FinishQuitting
             Game.KeyboardKeyDownEvent =|> fun evt ->
                if evt.Data.KeyboardKey = KeyboardKey.Up && not evt.Data.Repeated
                then Jump :> Signal
                else Nop :> Signal]

        // here we handle the above messages
        override this.Message (_, message, _, _) =
            match message with
            | StartQutting -> just Quitting
            | FinishQuitting -> just Quit

        // here we handle the above commands
        override this.Command (_, command, _, world) =
            match command with
            | Update ->
                let physicsId = Simulants.GameplayScenePlayer.GetPhysicsId world
                let world =
                    if World.isKeyboardKeyDown KeyboardKey.Left world then
                        if World.isBodyOnGround physicsId world
                        then World.applyBodyForce (v3 -2500.0f 0.0f 0.0f) physicsId world
                        else World.applyBodyForce (v3 -750.0f 0.0f 0.0f) physicsId world
                    elif World.isKeyboardKeyDown KeyboardKey.Right world then
                        if World.isBodyOnGround physicsId world
                        then World.applyBodyForce (v3 2500.0f 0.0f 0.0f) physicsId world
                        else World.applyBodyForce (v3 750.0f 0.0f 0.0f) physicsId world
                    else world
                just world
            | Jump ->
                let physicsId = Simulants.GameplayScenePlayer.GetPhysicsId world
                if World.isBodyOnGround physicsId world then
                    let world = World.playSound Constants.Audio.SoundVolumeDefault (asset "Gameplay" "Jump") world
                    let world = World.applyBodyForce (v3 0.0f 140000.0f 0.0f) physicsId world
                    just world
                else just world
            | PostUpdateEye ->
                if World.getAdvancing world then
                    let characterCenter = Simulants.GameplayScenePlayer.GetCenter world
                    let world = World.setEyeCenter2d characterCenter.V2 world
                    just world
                else just world
            | Nop -> just world

        // here we describe the content of the game including the level, the hud, and the player
        override this.Content (gameplay, _) =

            [// the gui group
             Content.group Simulants.GameplayGui.Name []
                [Content.button Simulants.GameplayGuiQuit.Name
                    [Entity.Position == v3 336.0f -216.0f 0.0f
                     Entity.Elevation == 10.0f
                     Entity.Text == "Quit"
                     Entity.ClickEvent => StartQutting]]

             // the scene group
             match gameplay with
             | Playing | Quitting ->
                Content.groupFromFile Simulants.GameplayScene.Name "Assets/Gameplay/Scene.nugroup" []
                    [Content.sideViewCharacter Simulants.GameplayScenePlayer.Name
                        [Entity.Position == v3 0.0f 24.0f 0.0f
                         Entity.Size == v3 108.0f 108.0f 0.0f]]
             | Quit -> ()]