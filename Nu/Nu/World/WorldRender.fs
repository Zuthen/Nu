﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2020.

namespace Nu
open System
open Prime
open Nu

[<AutoOpen; ModuleBinding>]
module WorldRender =

    type World with

        static member internal getRendererProcess world =
            world.Subsystems.RendererProcess

        static member internal withRendererProcess updater world =
            updater (World.getRendererProcess world)

        /// Enqueue a rendering message to the world.
        static member enqueueRenderMessage2d (message : RenderMessage2d) world =
            (World.getRendererProcess world).EnqueueMessage2d message
            world

        /// Enqueue multiple rendering messages to the world.
        static member enqueueRenderMessages2d (messages : RenderMessage2d seq) world =
            let rendererProcess = World.getRendererProcess world
            for message in messages do rendererProcess.EnqueueMessage2d message
            world
            
        /// Enqueue a layered message for rendering, bypassing enqueueRenderMessage for speed.
        static member enqueueRenderLayeredMessage2d (message : RenderLayeredMessage2d) world =
            (World.getRendererProcess world).EnqueueMessage2d (RenderLayeredMessage2d message)
            world

        /// Enqueue multiple layered rendering messages to the world, bypassing enqueueRenderMessage for speed.
        static member enqueueRenderLayeredMessages2d (messages : RenderLayeredMessage2d seq) world =
            let rendererProcess = World.getRendererProcess world
            for message in messages do rendererProcess.EnqueueMessage2d (RenderLayeredMessage2d message)
            world

        /// Hint that a rendering asset package with the given name should be loaded. Should be
        /// used to avoid loading assets at inconvenient times (such as in the middle of game play!)
        [<FunctionBinding>]
        static member hintRenderPackageUse2d packageName world =
            let hintRenderPackageUseMessage = HintRenderPackageUseMessage2d packageName
            World.enqueueRenderMessage2d hintRenderPackageUseMessage world

        /// Hint that a rendering package should be unloaded since its assets will not be used
        /// again (or until specified via World.hintRenderPackageUse).
        [<FunctionBinding>]
        static member hintRenderPackageDisuse2d packageName world =
            let hintRenderPackageDisuseMessage = HintRenderPackageDisuseMessage2d packageName
            World.enqueueRenderMessage2d hintRenderPackageDisuseMessage world

        /// Send a message to the renderer to reload its rendering assets.
        [<FunctionBinding>]
        static member reloadRenderAssets2d world =
            let reloadRenderAssetsMessage = ReloadRenderAssetsMessage2d
            World.enqueueRenderMessage2d reloadRenderAssetsMessage world

        /// Enqueue a rendering message to the world.
        static member enqueueRenderMessage3d (message : RenderMessage3d) world =
            (World.getRendererProcess world).EnqueueMessage3d message
            world

        /// Enqueue multiple rendering messages to the world.
        static member enqueueRenderMessages3d (messages : RenderMessage3d seq) world =
            let rendererProcess = World.getRendererProcess world
            for message in messages do rendererProcess.EnqueueMessage3d message
            world