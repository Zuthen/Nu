﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2020.

namespace Nu
open System
open System.Numerics
open Nito.Collections
open TiledSharp
open Prime
open Nu
open Nu.Declarative

[<AutoOpen>]
module DeclarativeOperators2 =

    type World with

        static member internal tryAttachModel (modelValue : 'model) modelName (simulant : Simulant) world =
            match World.tryGetProperty modelName simulant world with
            | None ->
                let property = { DesignerType = typeof<'model>; DesignerValue = modelValue }
                let property = { PropertyType = typeof<DesignerProperty>; PropertyValue = property }
                World.attachProperty modelName true property simulant world
            | Some _ -> world

        static member internal actualizeView view world =
            match view with
            | Render (elevation, positionY, assetTag, descriptor) ->
                let layeredDescriptor = { Elevation = elevation; PositionY = positionY; AssetTag = AssetTag.generalize assetTag; RenderDescriptor = descriptor }
                World.enqueueRenderMessage (LayeredDescriptorMessage layeredDescriptor) world
            | PlaySound (volume, assetTag) -> World.playSound volume assetTag world
            | PlaySong (fade, volume, assetTag) -> World.playSong fade volume assetTag world
            | FadeOutSong fade -> World.fadeOutSong fade world
            | StopSong -> World.stopSong world
            | Tag _ -> world
            | Views views ->
                Array.fold (fun world view -> World.actualizeView view world) world views

[<AutoOpen>]
module FacetModule =

    type World with

        static member internal trySignalEntityFacet (signalObj : obj) facetName (entity : Entity) world =
            let facets = entity.GetFacets world
            match Array.tryFind (fun facet -> getTypeName facet = facetName) facets with
            | Some (:? Facet<'model, 'message, 'command> as facet) ->
                match signalObj with
                | :? Signal<'message, 'command> as signal ->
                    Signal.processSignal facet.Message facet.Command (entity.FacetModel<'model> facet.ModelName) signal entity world
                | _ -> Log.info "Incorrect signal type returned from event binding."; world
            | _ -> Log.info "Failed to send signal to entity facet."; world

        static member internal signalEntityFacet<'model, 'message, 'command> signal facetName (entity : Entity) world =
            let facets = entity.GetFacets world
            match Array.tryFind (fun facet -> getTypeName facet = facetName) facets with
            | Some (:? Facet<'model, 'message, 'command> as facet) ->
                Signal.processSignal facet.Message facet.Command (entity.FacetModel<'model> facet.ModelName) signal entity world
            | _ -> Log.info "Failed to send signal to entity."; world

    and Entity with
    
        member this.GetFacetModel<'model> modelName world =
            this.Get<'model> modelName world

        member this.SetFacetModel<'model> modelName (value : 'model) world =
            this.Set<'model> modelName value world

        member this.UpdateFacetModel<'model> modelName updater world =
            this.SetFacetModel<'model> modelName (updater this.GetFacetModel<'model> modelName world) world

        member this.FacetModel<'model> modelName =
            lens<'model> modelName (this.GetFacetModel<'model> modelName) (this.SetFacetModel<'model> modelName) this

        member this.TrySignalEntityFacet<'model, 'message, 'command> signal facetName world =
            World.trySignalEntityFacet signal facetName this world

        member this.SignalEntityFacet<'model, 'message, 'command> signal facetName world =
            World.signalEntityFacet<'model, 'message, 'command> signal facetName this world

    and [<AbstractClass>] Facet<'model, 'message, 'command> (initial : 'model) =
        inherit Facet ()

        let mutable modelNameOpt =
            Unchecked.defaultof<string>

        member this.ModelName =
            if isNull modelNameOpt then modelNameOpt <- getTypeName this + "Model"
            modelNameOpt
            
        member this.GetModel (entity : Entity) world : 'model =
            entity.GetFacetModel<'model> this.ModelName world

        member this.SetModel (model : 'model) (entity : Entity) world =
            entity.SetFacetModel<'model> this.ModelName model world

        member this.Model (entity : Entity) =
            lens this.ModelName (this.GetModel entity) (flip this.SetModel entity) entity

        override this.Register (entity, world) =
            let world = World.tryAttachModel initial this.ModelName entity world
            let channels = this.Channel (this.Model entity, entity)
            let world = Signal.processChannels this.Message this.Command (this.Model entity) channels entity world
            let content = this.Content (this.Model entity, entity)
            let world =
                List.fold (fun world content ->
                    World.expandEntityContent content (FacetOrigin (entity, getTypeName this)) entity entity.Parent world |> snd)
                    world content
            let initializers = this.Initializers (this.Model entity, entity)
            List.fold (fun world initializer ->
                match initializer with
                | PropertyDefinition def ->
                    let propertyName = def.PropertyName
                    let alwaysPublish = Reflection.isPropertyAlwaysPublishByName propertyName
                    let property = { PropertyType = def.PropertyType; PropertyValue = PropertyExpr.eval def.PropertyExpr world }
                    World.setProperty def.PropertyName alwaysPublish property entity world
                | EventHandlerDefinition (handler, partialAddress) ->
                    let eventAddress = partialAddress --> entity
                    World.monitor (fun (evt : Event) world ->
                        let world = WorldModule.trySignalFacet (handler evt) (getTypeName this) entity world
                        (Cascade, world))
                        eventAddress (entity :> Simulant) world
                | BindDefinition (left, right) ->
                    WorldModule.bind5 entity left right world)
                world initializers

        override this.Actualize (entity, world) =
            let view = this.View (this.GetModel entity world, entity, world)
            World.actualizeView view world

        override this.TrySignal (signalObj, entity, world) =
            match signalObj with
            | :? Signal<'message, obj> as signal -> entity.SignalEntityFacet<'model, 'message, 'command> (match signal with Message message -> msg message | _ -> failwithumf ()) (getTypeName this) world
            | :? Signal<obj, 'command> as signal -> entity.SignalEntityFacet<'model, 'message, 'command> (match signal with Command command -> cmd command | _ -> failwithumf ()) (getTypeName this) world
            | _ -> Log.info "Incorrect signal type returned from event binding."; world

        abstract member Initializers : Lens<'model, World> * Entity -> PropertyInitializer list
        default this.Initializers (_, _) = []

        abstract member Channel : Lens<'model, World> * Entity -> Channel<'message, 'command, Entity, World> list
        default this.Channel (_, _) = []

        abstract member Message : 'model * 'message * Entity * World -> Signal<'message, 'command> list * 'model
        default this.Message (model, _, _, _) = just model

        abstract member Command : 'model * 'command * Entity * World -> Signal<'message, 'command> list * World
        default this.Command (_, _, _, world) = just world

        abstract member Content : Lens<'model, World> * Entity -> EntityContent list
        default this.Content (_, _) = []

        abstract member View : 'model * Entity * World -> View
        default this.View (_, _, _) = View.empty

[<AutoOpen>]
module BasicEmitterFacetModule =

    type Entity with

        member this.GetSelfDestruct world : bool = this.Get Property? SelfDestruct world
        member this.SetSelfDestruct (value : bool) world = this.SetFast Property? SelfDestruct false value world
        member this.SelfDestruct = lens Property? SelfDestruct this.GetSelfDestruct this.SetSelfDestruct this
        member this.GetEmitterOffset world : Vector2 = this.Get Property? EmitterOffset world
        member this.SetEmitterOffset (value : Vector2) world = this.SetFast Property? EmitterOffset true value world
        member this.EmitterOffset = lens Property? EmitterOffset this.GetEmitterOffset this.SetEmitterOffset this
        member this.GetEmitterTwist world : single = this.Get Property? EmitterTwist world
        member this.SetEmitterTwist (value : single) world = this.SetFast Property? EmitterTwist true value world
        member this.EmitterTwist = lens Property? EmitterTwist this.GetEmitterTwist this.SetEmitterTwist this
        member this.GetEmitterGravity world : Vector2 = this.Get Property? EmitterGravity world
        member this.SetEmitterGravity (value : Vector2) world = this.SetFast Property? EmitterGravity true value world
        member this.EmitterGravity = lens Property? EmitterGravity this.GetEmitterGravity this.SetEmitterGravity this
        member this.GetEmitterImage world : Image AssetTag = this.Get Property? EmitterImage world
        member this.SetEmitterImage (value : Image AssetTag) world = this.SetFast Property? EmitterImage true value world
        member this.EmitterImage = lens Property? EmitterImage this.GetEmitterImage this.SetEmitterImage this
        member this.GetEmitterBlend world : Blend = this.Get Property? EmitterBlend world
        member this.SetEmitterBlend (value : Blend) world = this.SetFast Property? EmitterBlend true value world
        member this.EmitterBlend = lens Property? EmitterBlend this.GetEmitterBlend this.SetEmitterBlend this
        member this.GetEmitterLifeTimeOpt world : int64 = this.Get Property? EmitterLifeTimeOpt world
        member this.SetEmitterLifeTimeOpt (value : int64) world = this.SetFast Property? EmitterLifeTimeOpt true value world
        member this.EmitterLifeTimeOpt = lens Property? EmitterLifeTimeOpt this.GetEmitterLifeTimeOpt this.SetEmitterLifeTimeOpt this
        member this.GetParticleLifeTimeOpt world : int64 = this.Get Property? ParticleLifeTimeOpt world
        member this.SetParticleLifeTimeOpt (value : int64) world = this.SetFast Property? ParticleLifeTimeOpt true value world
        member this.ParticleLifeTimeOpt = lens Property? ParticleLifeTimeOpt this.GetParticleLifeTimeOpt this.SetParticleLifeTimeOpt this
        member this.GetParticleRate world : single = this.Get Property? ParticleRate world
        member this.SetParticleRate (value : single) world = this.SetFast Property? ParticleRate true value world
        member this.ParticleRate = lens Property? ParticleRate this.GetParticleRate this.SetParticleRate this
        member this.GetParticleMax world : int = this.Get Property? ParticleMax world
        member this.SetParticleMax (value : int) world = this.SetFast Property? ParticleMax true value world
        member this.ParticleMax = lens Property? ParticleMax this.GetParticleMax this.SetParticleMax this
        member this.GetBasicParticleSeed world : Particles.BasicParticle = this.Get Property? BasicParticleSeed world
        member this.SetBasicParticleSeed (value : Particles.BasicParticle) world = this.SetFast Property? BasicParticleSeed true value world
        member this.BasicParticleSeed = lens Property? BasicParticleSeed this.GetBasicParticleSeed this.SetBasicParticleSeed this
        member this.GetEmitterConstraint world : Particles.Constraint = this.Get Property? EmitterConstraint world
        member this.SetEmitterConstraint (value : Particles.Constraint) world = this.SetFast Property? EmitterConstraint true value world
        member this.EmitterConstraint = lens Property? EmitterConstraint this.GetEmitterConstraint this.SetEmitterConstraint this
        member this.GetEmitterName world : string = this.Get Property? EmitterName world
        member this.SetEmitterName (value : string) world = this.SetFast Property? EmitterName true value world
        member this.EmitterName = lens Property? EmitterName this.GetEmitterName this.SetEmitterName this
        member this.GetParticleSystem world : Particles.ParticleSystem = this.Get Property? ParticleSystem world
        member this.SetParticleSystem (value : Particles.ParticleSystem) world = this.SetFast Property? ParticleSystem false value world
        member this.ParticleSystem = lens Property? ParticleSystem this.GetParticleSystem this.SetParticleSystem this

    type BasicEmitterFacet () =
        inherit Facet ()

        static let updateEmitter (descriptor : Particles.BasicEmitterDescriptor) (entity : Entity) world =
            let particleSystem = entity.GetParticleSystem world
            let emitter = Map.find typeof<Particles.BasicEmitter>.Name particleSystem.Emitters :?> Particles.BasicEmitter
            let emitter =
                let entityPosition = entity.GetPosition world
                if v2Neq emitter.Body.Position (entityPosition + descriptor.Body.Position)
                then { emitter with Body = { emitter.Body with Position = entityPosition + descriptor.Body.Position }}
                else emitter
            let emitter =
                let entityRotation = entity.GetRotation world
                if emitter.Body.Rotation <> entityRotation + descriptor.Body.Rotation
                then { emitter with Body = { emitter.Body with Rotation = entityRotation + descriptor.Body.Rotation }}
                else emitter
            let emitter = if v2Neq emitter.Body.Gravity descriptor.Body.Gravity then { emitter with Body = { emitter.Body with Gravity = descriptor.Body.Gravity }} else emitter
            let emitter = if assNeq emitter.Image descriptor.Image then { emitter with Image = descriptor.Image } else emitter
            let emitter = if emitter.Blend <> descriptor.Blend then { emitter with Blend = descriptor.Blend } else emitter
            let emitter = if emitter.Life.LifeTimeOpt <> descriptor.LifeTimeOpt then { emitter with Life = { emitter.Life with LifeTimeOpt = descriptor.LifeTimeOpt }} else emitter
            let emitter = if emitter.ParticleLifeTimeOpt <> descriptor.ParticleLifeTimeOpt then { emitter with ParticleLifeTimeOpt = descriptor.ParticleLifeTimeOpt } else emitter
            let emitter = if emitter.ParticleRate <> descriptor.ParticleRate then { emitter with ParticleRate = descriptor.ParticleRate } else emitter
            let emitter = if emitter.ParticleBuffer.Length <> descriptor.ParticleMax then Particles.BasicEmitter.resize descriptor.ParticleMax emitter else emitter
            let emitter = if emitter.ParticleSeed <> descriptor.ParticleSeed then { emitter with ParticleSeed = descriptor.ParticleSeed } else emitter
            let emitter = if emitter.Constraint <> descriptor.Constraint then { emitter with Constraint = descriptor.Constraint } else emitter
            let particleSystem = { particleSystem with Emitters = Map.add typeof<Particles.BasicEmitter>.Name (emitter :> Particles.Emitter) particleSystem.Emitters }
            entity.SetParticleSystem particleSystem world

        static let handleBoundsChanged evt world =
            let entity = evt.Subscriber : Entity
            let particleSystem = entity.GetParticleSystem world
            let emitter = Map.find typeof<Particles.BasicEmitter>.Name particleSystem.Emitters :?> Particles.BasicEmitter
            let emitter =
                let entityPosition = entity.GetPosition world + entity.GetEmitterOffset world
                if v2Neq emitter.Body.Position entityPosition
                then { emitter with Body = { emitter.Body with Position = entityPosition }}
                else emitter
            let emitter =
                let entityRotation = entity.GetRotation world + entity.GetEmitterTwist world
                if emitter.Body.Rotation <> entityRotation
                then { emitter with Body = { emitter.Body with Rotation = entityRotation }}
                else emitter
            let particleSystem = { particleSystem with Emitters = Map.add typeof<Particles.BasicEmitter>.Name (emitter :> Particles.Emitter) particleSystem.Emitters }
            entity.SetParticleSystem particleSystem world

        static member Properties =
            [define Entity.PublishChanges true
             define Entity.SelfDestruct false
             define Entity.EmitterOffset v2Zero
             define Entity.EmitterTwist 0.0f
             define Entity.EmitterGravity Constants.Engine.Gravity
             define Entity.EmitterImage Assets.Default.Image
             define Entity.EmitterBlend Additive
             define Entity.EmitterLifeTimeOpt 60L
             define Entity.ParticleLifeTimeOpt 60L
             define Entity.ParticleRate 1.0f
             define Entity.ParticleMax 60
             define Entity.BasicParticleSeed Unchecked.defaultof<Particles.BasicParticle>
             define Entity.EmitterConstraint Particles.NoConstraint
             define Entity.ParticleSystem Particles.ParticleSystem.empty]

        override this.Register (entity, world) =
            // TODO: implement.
            world

        override this.Update (entity, world) =
            let time = World.getTickTime world
            let particleSystem = entity.GetParticleSystem world
            let (particleSystem, output) = Particles.ParticleSystem.run time Particles.NoConstraint particleSystem
            entity.SetParticleSystem particleSystem world

        override this.Actualize (entity, world) =
            let particleSystem = entity.GetParticleSystem world
            let particlesDescriptors = Particles.ParticleSystem.toParticleDescriptors particleSystem
            let particlesMessages =
                List.map (fun (descriptor : ParticlesDescriptor) ->
                    LayeredDescriptorMessage
                        { Elevation = descriptor.Elevation
                          PositionY = descriptor.PositionY
                          AssetTag = AssetTag.generalize descriptor.Image
                          RenderDescriptor = ParticlesDescriptor descriptor })
                    particlesDescriptors
            World.enqueueRenderMessages particlesMessages world

[<AutoOpen>]
module BasicEmittersFacetModule =

    type Entity with

        member this.GetBasicEmitterSymbols world : Symbol AssetTag list = this.Get Property? BasicEmitterSymbols world
        member this.SetBasicEmitterSymbols (value : Symbol AssetTag list) world = this.SetFast Property? BasicEmitterSymbols true value world
        member this.BasicEmitterSymbols = lens Property? BasicEmitterSymbols this.GetBasicEmitterSymbols this.SetBasicEmitterSymbols this
        member this.GetBasicEmitterDescriptors world : Particles.BasicEmitterDescriptors = this.Get Property? BasicEmitterDescriptors world // NOTE: Body.Position and Rotation will act as offsets.
        member this.SetBasicEmitterDescriptors (value : Particles.BasicEmitterDescriptors) world = this.SetFast Property? BasicEmitterDescriptors true value world
        member this.BasicEmitterDescriptors = lens Property? BasicEmitterDescriptors this.GetBasicEmitterDescriptors this.SetBasicEmitterDescriptors this

[<AutoOpen>]
module EffectFacetModule =

    type EffectTags =
        Map<string, Effects.Slice>

    type Entity with

        member this.GetEffectSymbolOpt world : Symbol AssetTag option = this.Get Property? EffectSymbolOpt world
        member this.SetEffectSymbolOpt (value : Symbol AssetTag option) world = this.SetFast Property? EffectSymbolOpt true value world
        member this.EffectSymbolOpt = lens Property? EffectSymbolOpt this.GetEffectSymbolOpt this.SetEffectSymbolOpt this
        member this.GetEffectStartTimeOpt world : int64 option = this.Get Property? EffectStartTimeOpt world
        member this.SetEffectStartTimeOpt (value : int64 option) world = this.SetFast Property? EffectStartTimeOpt false value world
        member this.EffectStartTimeOpt = lens Property? EffectStartTimeOpt this.GetEffectStartTimeOpt this.SetEffectStartTimeOpt this
        member this.GetEffectDefinitions world : Effects.Definitions = this.Get Property? EffectDefinitions world
        member this.SetEffectDefinitions (value : Effects.Definitions) world = this.SetFast Property? EffectDefinitions false value world
        member this.EffectDefinitions = lens Property? EffectDefinitions this.GetEffectDefinitions this.SetEffectDefinitions this
        member this.GetEffect world : Effect = this.Get Property? Effect world
        member this.SetEffect (value : Effect) world = this.SetFast Property? Effect false value world
        member this.Effect = lens Property? Effect this.GetEffect this.SetEffect this
        member this.GetEffectOffset world : Vector2 = this.Get Property? EffectOffset world
        member this.SetEffectOffset (value : Vector2) world = this.SetFast Property? EffectOffset false value world
        member this.EffectOffset = lens Property? EffectOffset this.GetEffectOffset this.SetEffectOffset this
        member this.GetEffectSliceOffset world : Vector2 = this.Get Property? EffectSliceOffset world
        member this.SetEffectSliceOffset (value : Vector2) world = this.SetFast Property? EffectSliceOffset false value world
        member this.EffectSliceOffset = lens Property? EffectSliceOffset this.GetEffectSliceOffset this.SetEffectSliceOffset this
        member this.GetEffectTags world : EffectTags = this.Get Property? EffectTags world
        member private this.SetEffectTags (value : EffectTags) world = this.SetFast Property? EffectTags false value world
        member this.EffectTags = lensReadOnly Property? EffectTags this.GetEffectTags this
        member this.GetEffectHistoryMax world : int = this.Get Property? EffectHistoryMax world
        member this.SetEffectHistoryMax (value : int) world = this.SetFast Property? EffectHistoryMax false value world
        member this.EffectHistoryMax = lens Property? EffectHistoryMax this.GetEffectHistoryMax this.SetEffectHistoryMax this
        member this.GetEffectHistory world : Effects.Slice Deque = this.Get Property? EffectHistory world
        member private this.SetEffectHistory (value : Effects.Slice Deque) world = this.SetFast Property? EffectHistory false value world
        member this.EffectHistory = lensReadOnly Property? EffectHistory this.GetEffectHistory this

        /// The start time of the effect, or zero if none.
        member this.GetEffectStartTime world =
            match this.GetEffectStartTimeOpt world with
            | Some effectStartTime -> effectStartTime
            | None -> 0L

        /// The time relative to the start of the effect.
        member this.GetEffectTime world =
            let startTime = this.GetEffectStartTime world
            let time = World.getTickTime world
            time - startTime

    type EffectFacet () =
        inherit Facet ()

        static let setEffect effectSymbolOpt (entity : Entity) world =
            match effectSymbolOpt with
            | Some effectSymbol ->
                let symbolLoadMetadata = { ImplicitDelimiters = false; StripCsvHeader = false }
                match World.assetTagToValueOpt<Effect> effectSymbol symbolLoadMetadata world with
                | Some effect -> entity.SetEffect effect world
                | None -> world
            | None -> world

        static let handleEffectsChanged evt world =
            let entity = evt.Subscriber : Entity
            let world = setEffect (entity.GetEffectSymbolOpt world) entity world
            (Cascade, world)

        static let handleAssetsReload evt world =
            let entity = evt.Subscriber : Entity
            let world = setEffect (entity.GetEffectSymbolOpt world) entity world
            (Cascade, world)

        static member Properties =
            [define Entity.PublishChanges true
             define Entity.SelfDestruct false
             define Entity.EffectSymbolOpt None
             define Entity.EffectStartTimeOpt None
             define Entity.EffectDefinitions Map.empty
             define Entity.Effect Effect.empty
             define Entity.EffectOffset (Vector2 0.5f)
             define Entity.EffectSliceOffset (Vector2 0.5f)
             define Entity.EffectTags Map.empty
             define Entity.EffectHistoryMax Constants.Effects.EffectHistoryMaxDefault
             variable Entity.EffectHistory (fun _ -> Deque<Effects.Slice> (inc Constants.Effects.EffectHistoryMaxDefault))]

        override this.Actualize (entity, world) =

            // evaluate effect if visible
            if entity.GetVisible world && entity.GetInView world then

                // set up effect system to evaluate effect
                let world = entity.SetEffectTags Map.empty world
                let effect = entity.GetEffect world
                let effectTime = entity.GetEffectTime world
                let effectAbsolute = entity.GetAbsolute world
                let effectSlice =
                    { Effects.Position = entity.GetPosition world + Vector2.Multiply (entity.GetSize world, entity.GetEffectOffset world)
                      Effects.Size = entity.GetSize world
                      Effects.Rotation = entity.GetRotation world
                      Effects.Elevation = entity.GetElevation world
                      Effects.Offset = entity.GetEffectSliceOffset world
                      Effects.InsetOpt = None
                      Effects.Color = Color.White
                      Effects.Glow = Color.Zero
                      Effects.Enabled = true
                      Effects.Volume = Constants.Audio.SoundVolumeDefault }
                let effectHistory = entity.GetEffectHistory world
                let effectDefinitions = entity.GetEffectDefinitions world
                let effectSystem = EffectSystem.make effectAbsolute effectTime effectDefinitions

                // evaluate effect with effect system
                let (view, _) = EffectSystem.eval effect effectSlice effectHistory effectSystem

                // actualize effect view
                let world = World.actualizeView view world

                // store tags
                let tags =
                    view |>
                    View.toArray |>
                    Array.map (function Tag (name, value) -> Some (name, value :?> Effects.Slice) | _ -> None) |>
                    Array.definitize |> Map.ofArray
                let world = entity.SetEffectTags tags world

                // update effect history in-place
                effectHistory.AddToFront effectSlice
                if effectHistory.Count > entity.GetEffectHistoryMax world then effectHistory.RemoveFromBack () |> ignore
                world

            // no need to evaluate non-visible effect
            else world

        override this.Update (entity, world) =
            let effect = entity.GetEffect world
            match (entity.GetSelfDestruct world, effect.LifeTimeOpt) with
            | (true, Some lifetime) ->
                let effectTime = entity.GetEffectTime world
                if effectTime >= dec lifetime // NOTE: dec keeps effect from actualizing past the last frame when it is created mid-frame
                then World.destroyEntity entity world
                else world
            | (_, _) -> world

        override this.Register (entity, world) =
            let effectStartTime = Option.getOrDefault (World.getTickTime world) (entity.GetEffectStartTimeOpt world)
            let world = entity.SetEffectStartTimeOpt (Some effectStartTime) world
            let world = World.monitor handleEffectsChanged (entity.GetChangeEvent Property? Effects) entity world
            World.monitor handleAssetsReload Events.AssetsReload entity world

//[<AutoOpen>]
//module EffectsFacetModule =
//
//    type Entity with
//
//        member this.GetEffectSymbols world : Symbol AssetTag list = this.Get Property? EffectSymbols world
//        member this.SetEffectSymbols (value : Symbol AssetTag list) world = this.SetFast Property? EffectSymbols true value world
//        member this.EffectSymbols = lens Property? EffectSymbols this.GetEffectSymbols this.SetEffectSymbols this
//        member this.GetEffectDescriptors world : EffectDescriptors.EffectDescriptors = this.Get Property? EffectDescriptors world
//        member this.SetEffectDescriptors (value : EffectDescriptors.EffectDescriptors) world = this.SetFast Property? EffectDescriptors true value world
//        member this.EffectDescriptors = lens Property? EffectDescriptors this.GetEffectDescriptors this.SetEffectDescriptors this

[<AutoOpen>]
module ScriptFacetModule =

    type Entity with
        member this.GetScriptOpt world : Symbol AssetTag option = this.Get Property? ScriptOpt world
        member this.SetScriptOpt (value : Symbol AssetTag option) world = this.SetFast Property? ScriptOpt true value world
        member this.ScriptOpt = lens Property? ScriptOpt this.GetScriptOpt this.SetScriptOpt this
        member this.GetScript world : Scripting.Expr array = this.Get Property? Script world
        member this.SetScript (value : Scripting.Expr array) world = this.SetFast Property? Script true value world
        member this.Script = lens Property? Script this.GetScript this.SetScript this
        member internal this.GetScriptUnsubscriptions world : Unsubscription list = this.Get Property? ScriptUnsubscriptions world
        member internal this.SetScriptUnsubscriptions (value : Unsubscription list) world = this.SetFast Property? ScriptUnsubscriptions false value world
        member internal this.ScriptUnsubscriptions = lens Property? ScriptUnsubscriptions this.GetScriptUnsubscriptions this.SetScriptUnsubscriptions this
        member this.GetRegisterScript world : Scripting.Expr = this.Get Property? RegisterScript world
        member this.SetRegisterScript (value : Scripting.Expr) world = this.SetFast Property? RegisterScript true value world
        member this.RegisterScript = lens Property? RegisterScript this.GetRegisterScript this.SetRegisterScript this
        member this.GetUnregisterScript world : Scripting.Expr = this.Get Property? UnregisterScript world
        member this.SetUnregisterScript (value : Scripting.Expr) world = this.SetFast Property? UnregisterScript false value world
        member this.UnregisterScript = lens Property? UnregisterScript this.GetUnregisterScript this.SetUnregisterScript this
        member this.GetUpdateScript world : Scripting.Expr = this.Get Property? UpdateScript world
        member this.SetUpdateScript (value : Scripting.Expr) world = this.SetFast Property? UpdateScript false value world
        member this.UpdateScript = lens Property? UpdateScript this.GetUpdateScript this.SetUpdateScript this
        member this.GetPostUpdateScript world : Scripting.Expr = this.Get Property? PostUpdateScript world
        member this.SetPostUpdateScript (value : Scripting.Expr) world = this.SetFast Property? PostUpdateScript false value world
        member this.PostUpdateScript = lens Property? PostUpdateScript this.GetPostUpdateScript this.SetPostUpdateScript this

    type ScriptFacet () =
        inherit Facet ()

        static let handleScriptChanged evt world =
            let entity = evt.Subscriber : Entity
            let script = entity.GetScript world
            let scriptFrame = Scripting.DeclarationFrame HashIdentity.Structural
            let world = World.setEntityScriptFrame scriptFrame entity world
            let world = evalManyWithLogging script scriptFrame entity world |> snd'
            (Cascade, world)

        static let handleRegisterScriptChanged evt world =
            let entity = evt.Subscriber : Entity
            let world = World.unregisterEntity entity world
            let world = World.registerEntity entity world
            (Cascade, world)

        static member Properties =
            [define Entity.PublishChanges true
             define Entity.ScriptOpt None
             define Entity.Script [||]
             define Entity.ScriptUnsubscriptions []
             define Entity.RegisterScript Scripting.Unit
             define Entity.UnregisterScript Scripting.Unit
             define Entity.UpdateScript Scripting.Unit
             define Entity.PostUpdateScript Scripting.Unit]

        override this.Register (entity, world) =
            let world = World.evalWithLogging (entity.GetRegisterScript world) (entity.GetScriptFrame world) entity world |> snd'
            let world = World.monitor handleScriptChanged (entity.GetChangeEvent Property? Script) entity world
            let world = World.monitor handleRegisterScriptChanged (entity.GetChangeEvent Property? RegisterScript) entity world
            world

        override this.Unregister (entity, world) =
            World.evalWithLogging (entity.GetUnregisterScript world) (entity.GetScriptFrame world) entity world |> snd'

        override this.Update (entity, world) =
            World.evalWithLogging (entity.GetUpdateScript world) (entity.GetScriptFrame world) entity world |> snd'

#if !DISABLE_ENTITY_POST_UPDATE
        override this.PostUpdate (entity, world) =
            World.evalWithLogging (entity.GetPostUpdateScript world) (entity.GetScriptFrame world) entity world |> snd'
#endif

[<AutoOpen>]
module TextFacetModule =

    type Entity with
        member this.GetText world : string = this.Get Property? Text world
        member this.SetText (value : string) world = this.SetFast Property? Text false value world
        member this.Text = lens Property? Text this.GetText this.SetText this
        member this.GetFont world : Font AssetTag = this.Get Property? Font world
        member this.SetFont (value : Font AssetTag) world = this.SetFast Property? Font false value world
        member this.Font = lens Property? Font this.GetFont this.SetFont this
        member this.GetMargins world : Vector2 = this.Get Property? Margins world
        member this.SetMargins (value : Vector2) world = this.SetFast Property? Margins false value world
        member this.Margins = lens Property? Margins this.GetMargins this.SetMargins this
        member this.GetJustification world : Justification = this.Get Property? Justification world
        member this.SetJustification (value : Justification) world = this.SetFast Property? Justification false value world
        member this.Justification = lens Property? Justification this.GetJustification this.SetJustification this
        member this.GetTextColor world : Color = this.Get Property? TextColor world
        member this.SetTextColor (value : Color) world = this.SetFast Property? TextColor false value world
        member this.TextColor = lens Property? TextColor this.GetTextColor this.SetTextColor this
        member this.GetTextDisabledColor world : Color = this.Get Property? TextDisabledColor world
        member this.SetTextDisabledColor (value : Color) world = this.SetFast Property? TextDisabledColor false value world
        member this.TextDisabledColor = lens Property? TextDisabledColor this.GetTextDisabledColor this.SetTextDisabledColor this

    type TextFacet () =
        inherit Facet ()

        static member Properties =
            [define Entity.Text ""
             define Entity.Font Assets.Default.Font
             define Entity.Margins Vector2.Zero
             define Entity.Justification (Justified (JustifyCenter, JustifyMiddle))
             define Entity.TextColor Color.Black
             define Entity.TextDisabledColor (Color (byte 64, byte 64, byte 64, byte 192))]

        override this.Actualize (entity, world) =
            let text = entity.GetText world
            if entity.GetVisible world && not (String.IsNullOrWhiteSpace text) then
                let transform =
                    { Position = entity.GetPosition world + entity.GetMargins world
                      Size = entity.GetSize world - entity.GetMargins world * 2.0f
                      Rotation = 0.0f
                      Elevation = entity.GetElevation world + 0.5f
                      Flags = entity.GetFlags world }
                World.enqueueRenderMessage
                    (LayeredDescriptorMessage
                        { Elevation = transform.Elevation
                          PositionY = transform.Position.Y
                          AssetTag = entity.GetFont world |> AssetTag.generalize
                          RenderDescriptor =
                            TextDescriptor
                                { Transform = transform
                                  Text = text
                                  Font = entity.GetFont world
                                  Color = if entity.GetEnabled world then entity.GetTextColor world else entity.GetTextDisabledColor world
                                  Justification = entity.GetJustification world }})
                    world
            else world

[<AutoOpen>]
module RigidBodyFacetModule =

    type Entity with
        member this.GetBodyEnabled world : bool = this.Get Property? BodyEnabled world
        member this.SetBodyEnabled (value : bool) world = this.SetFast Property? BodyEnabled false value world
        member this.BodyEnabled = lens Property? BodyEnabled this.GetBodyEnabled this.SetBodyEnabled this
        member this.GetBodyType world : BodyType = this.Get Property? BodyType world
        member this.SetBodyType (value : BodyType) world = this.SetFast Property? BodyType false value world
        member this.BodyType = lens Property? BodyType this.GetBodyType this.SetBodyType this
        member this.GetAwake world : bool = this.Get Property? Awake world
        member this.SetAwake (value : bool) world = this.SetFast Property? Awake false value world
        member this.Awake = lens Property? Awake this.GetAwake this.SetAwake this
        member this.GetDensity world : single = this.Get Property? Density world
        member this.SetDensity (value : single) world = this.SetFast Property? Density false value world
        member this.Density = lens Property? Density this.GetDensity this.SetDensity this
        member this.GetFriction world : single = this.Get Property? Friction world
        member this.SetFriction (value : single) world = this.SetFast Property? Friction false value world
        member this.Friction = lens Property? Friction this.GetFriction this.SetFriction this
        member this.GetRestitution world : single = this.Get Property? Restitution world
        member this.SetRestitution (value : single) world = this.SetFast Property? Restitution false value world
        member this.Restitution = lens Property? Restitution this.GetRestitution this.SetRestitution this
        member this.GetFixedRotation world : bool = this.Get Property? FixedRotation world
        member this.SetFixedRotation (value : bool) world = this.SetFast Property? FixedRotation false value world
        member this.FixedRotation = lens Property? FixedRotation this.GetFixedRotation this.SetFixedRotation this
        member this.GetAngularVelocity world : single = this.Get Property? AngularVelocity world
        member this.SetAngularVelocity (value : single) world = this.SetFast Property? AngularVelocity false value world
        member this.AngularVelocity = lens Property? AngularVelocity this.GetAngularVelocity this.SetAngularVelocity this
        member this.GetAngularDamping world : single = this.Get Property? AngularDamping world
        member this.SetAngularDamping (value : single) world = this.SetFast Property? AngularDamping false value world
        member this.AngularDamping = lens Property? AngularDamping this.GetAngularDamping this.SetAngularDamping this
        member this.GetLinearVelocity world : Vector2 = this.Get Property? LinearVelocity world
        member this.SetLinearVelocity (value : Vector2) world = this.SetFast Property? LinearVelocity false value world
        member this.LinearVelocity = lens Property? LinearVelocity this.GetLinearVelocity this.SetLinearVelocity this
        member this.GetLinearDamping world : single = this.Get Property? LinearDamping world
        member this.SetLinearDamping (value : single) world = this.SetFast Property? LinearDamping false value world
        member this.LinearDamping = lens Property? LinearDamping this.GetLinearDamping this.SetLinearDamping this
        member this.GetInertia world : single = this.Get Property? Inertia world
        member this.SetInertia (value : single) world = this.SetFast Property? Inertia false value world
        member this.Inertia = lens Property? Inertia this.GetInertia this.SetInertia this
        member this.GetGravityScale world : single = this.Get Property? GravityScale world
        member this.SetGravityScale (value : single) world = this.SetFast Property? GravityScale false value world
        member this.GravityScale = lens Property? GravityScale this.GetGravityScale this.SetGravityScale this
        member this.GetCollisionCategories world : string = this.Get Property? CollisionCategories world
        member this.SetCollisionCategories (value : string) world = this.SetFast Property? CollisionCategories false value world
        member this.CollisionCategories = lens Property? CollisionCategories this.GetCollisionCategories this.SetCollisionCategories this
        member this.GetCollisionMask world : string = this.Get Property? CollisionMask world
        member this.SetCollisionMask (value : string) world = this.SetFast Property? CollisionMask false value world
        member this.CollisionMask = lens Property? CollisionMask this.GetCollisionMask this.SetCollisionMask this
        member this.GetBodyShape world : BodyShape = this.Get Property? BodyShape world
        member this.SetBodyShape (value : BodyShape) world = this.SetFast Property? BodyShape false value world
        member this.BodyShape = lens Property? BodyShape this.GetBodyShape this.SetBodyShape this
        member this.GetIgnoreCCD world : bool = this.Get Property? IgnoreCCD world
        member this.SetIgnoreCCD (value : bool) world = this.SetFast Property? IgnoreCCD false value world
        member this.IgnoreCCD = lens Property? IgnoreCCD this.GetIgnoreCCD this.SetIgnoreCCD this
        member this.GetIsBullet world : bool = this.Get Property? IsBullet world
        member this.SetIsBullet (value : bool) world = this.SetFast Property? IsBullet false value world
        member this.IsBullet = lens Property? IsBullet this.GetIsBullet this.SetIsBullet this
        member this.GetIsSensor world : bool = this.Get Property? IsSensor world
        member this.SetIsSensor (value : bool) world = this.SetFast Property? IsSensor false value world
        member this.IsSensor = lens Property? IsSensor this.GetIsSensor this.SetIsSensor this
        member this.GetPhysicsId world : PhysicsId = this.Get Property? PhysicsId world
        member this.PhysicsId = lensReadOnly Property? PhysicsId this.GetPhysicsId this
        member this.CollisionEvent = Events.Collision --> this
        member this.SeparationEvent = Events.Separation --> this

    type RigidBodyFacet () =
        inherit Facet ()

        static let getBodyShape (entity : Entity) world =
            World.localizeBodyShape (entity.GetSize world) (entity.GetBodyShape world) world

        static member Properties =
            [define Entity.PublishChanges true
             define Entity.BodyEnabled true
             define Entity.BodyType Dynamic
             define Entity.Awake true
             define Entity.Density Constants.Physics.NormalDensity
             define Entity.Friction 0.2f
             define Entity.Restitution 0.0f
             define Entity.FixedRotation false
             define Entity.AngularVelocity 0.0f
             define Entity.AngularDamping 0.0f
             define Entity.LinearVelocity Vector2.Zero
             define Entity.LinearDamping 0.0f
             define Entity.Inertia 0.0f
             define Entity.GravityScale 1.0f
             define Entity.CollisionCategories "1"
             define Entity.CollisionMask "@"
             define Entity.BodyShape (BodyBox { Extent = Vector2 0.5f; Center = Vector2.Zero; PropertiesOpt = None })
             define Entity.IgnoreCCD false
             define Entity.IsBullet false
             define Entity.IsSensor false
             computed Entity.PhysicsId (fun (entity : Entity) world -> { SourceId = entity.GetId world; CorrelationId = Gen.idEmpty }) None]

        override this.Register (entity, world) =
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? BodyEnabled) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? Transform) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? BodyType) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? Awake) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? Density) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? Friction) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? Restitution) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? AngularVelocity) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? AngularDamping) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? LinearVelocity) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? LinearDamping) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? Inertia) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? GravityScale) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? CollisionCategories) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? CollisionMask) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? BodyShape) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? IgnoreCCD) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? IsBullet) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? IsSensor) entity world
            world

        override this.RegisterPhysics (entity, world) =
            let bodyProperties = 
                { BodyId = (entity.GetPhysicsId world).CorrelationId
                  Position = entity.GetPosition world + entity.GetSize world * 0.5f
                  Rotation = entity.GetRotation world
                  BodyShape = getBodyShape entity world
                  BodyType = entity.GetBodyType world
                  Awake = entity.GetAwake world
                  Enabled = entity.GetBodyEnabled world
                  Density = entity.GetDensity world
                  Friction = entity.GetFriction world
                  Restitution = entity.GetRestitution world
                  FixedRotation = entity.GetFixedRotation world
                  AngularVelocity = entity.GetAngularVelocity world
                  AngularDamping = entity.GetAngularDamping world
                  LinearVelocity = entity.GetLinearVelocity world
                  LinearDamping = entity.GetLinearDamping world
                  Inertia = entity.GetInertia world
                  GravityScale = entity.GetGravityScale world
                  CollisionCategories = PhysicsEngine.categorizeCollisionMask (entity.GetCollisionCategories world)
                  CollisionMask = PhysicsEngine.categorizeCollisionMask (entity.GetCollisionMask world)
                  IgnoreCCD = entity.GetIgnoreCCD world
                  IsBullet = entity.GetIsBullet world
                  IsSensor = entity.GetIsSensor world }
            World.createBody entity (entity.GetId world) bodyProperties world

        override this.UnregisterPhysics (entity, world) =
            World.destroyBody (entity.GetPhysicsId world) world

[<AutoOpen>]
module JointFacetModule =

    type Entity with
        member this.GetJointDevice world : JointDevice = this.Get Property? JointDevice world
        member this.SetJointDevice (value : JointDevice) world = this.SetFast Property? JointDevice false value world
        member this.JointDevice = lens Property? JointDevice this.GetJointDevice this.SetJointDevice this

    type JointFacet () =
        inherit Facet ()

        static member Properties =
            [define Entity.PublishChanges true
             define Entity.JointDevice JointEmpty
             computed Entity.PhysicsId (fun (entity : Entity) world -> { SourceId = entity.GetId world; CorrelationId = Gen.idEmpty }) None]

        override this.Register (entity, world) =
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? Transform) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? JointDevice) entity world
            world

        override this.RegisterPhysics (entity, world) =
            let jointProperties =
                { JointId = (entity.GetPhysicsId world).CorrelationId
                  JointDevice = (entity.GetJointDevice world) }
            World.createJoint entity (entity.GetId world) jointProperties world

        override this.UnregisterPhysics (entity, world) =
            World.destroyJoint (entity.GetPhysicsId world) world

[<AutoOpen>]
module TileMapFacetModule =

    type Entity with
        member this.GetTileMap world : TileMap AssetTag = this.Get Property? TileMap world
        member this.SetTileMap (value : TileMap AssetTag) world = this.SetFast Property? TileMap false value world
        member this.TileMap = lens Property? TileMap this.GetTileMap this.SetTileMap this
        member this.GetTileLayerClearance world : single = this.Get Property? TileLayerClearance world
        member this.SetTileLayerClearance (value : single) world = this.SetFast Property? TileLayerClearance false value world
        member this.TileLayerClearance = lens Property? TileLayerClearance this.GetTileLayerClearance this.SetTileLayerClearance this
        member this.GetParallax world : single = this.Get Property? Parallax world
        member this.SetParallax (value : single) world = this.SetFast Property? Parallax false value world
        member this.Parallax = lens Property? Parallax this.GetParallax this.SetParallax this

    type TileMapFacet () =
        inherit Facet ()

        static member Properties =
            [define Entity.Omnipresent true
             define Entity.PublishChanges true
             define Entity.BodyEnabled true
             define Entity.Friction 0.0f
             define Entity.Restitution 0.0f
             define Entity.CollisionCategories "1"
             define Entity.CollisionMask "@"
             define Entity.TileMap Assets.Default.TileMap
             define Entity.TileLayerClearance 2.0f
             define Entity.Parallax 0.0f
             computed Entity.PhysicsId (fun (entity : Entity) world -> { SourceId = entity.GetId world; CorrelationId = Gen.idEmpty }) None]

        override this.Register (entity, world) =
            let world = entity.SetSize (entity.GetQuickSize world) world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? BodyEnabled) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? Transform) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? Friction) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? Restitution) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? CollisionCategories) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? CollisionMask) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? TileMap) entity world
            let world =
                World.monitor (fun _ world ->
                    let quickSize = entity.GetQuickSize world
                    let transform = entity.GetTransform world
                    let world = entity.SetTransformWithoutEvent { transform with Size = quickSize } world
                    (Cascade, entity.PropagatePhysics world))
                    (entity.ChangeEvent Property? TileMap)
                    entity
                    world
            world

        override this.RegisterPhysics (entity, world) =
            match TmxMap.tryGetTileMap (entity.GetTileMap world) world with
            | Some tileMap ->
                let tileMapPosition = entity.GetPosition world
                match TmxMap.tryGetDescriptor tileMapPosition tileMap with
                | Some tileMapDescriptor ->
                    let bodyProperties =
                        TmxMap.getBodyProperties
                            (entity.GetEnabled world)
                            (entity.GetFriction world)
                            (entity.GetRestitution world)
                            (entity.GetCollisionCategories world)
                            (entity.GetCollisionMask world)
                            (entity.GetPhysicsId world).CorrelationId
                            tileMapDescriptor
                    World.createBody entity (entity.GetId world) bodyProperties world
                | None -> world
            | None -> world

        override this.UnregisterPhysics (entity, world) =
            World.destroyBody (entity.GetPhysicsId world) world

        override this.Actualize (entity, world) =
            if entity.GetVisible world then
                match TmxMap.tryGetTileMap (entity.GetTileMap world) world with
                | Some tileMap ->
                    let absolute = entity.GetAbsolute world
                    let viewBounds = World.getViewBounds absolute world
                    let tileMapRenderMessages =
                        TmxMap.getRenderMessages
                            absolute
                            viewBounds
                            (entity.GetTileLayerClearance world)
                            (entity.GetPosition world)
                            (entity.GetElevation world)
                            (entity.GetParallax world)
                            tileMap
                    World.enqueueRenderMessages tileMapRenderMessages world
                | None -> world
            else world

        override this.GetQuickSize (entity, world) =
            match TmxMap.tryGetTileMap (entity.GetTileMap world) world with
            | Some tileMap -> TmxMap.getQuickSize tileMap
            | None -> Constants.Engine.EntitySizeDefault

[<AutoOpen>]
module TmxMapFacetModule =

    type Entity with
        member this.GetTmxMap world : TmxMap = this.Get Property? TmxMap world
        member this.SetTmxMap (value : TmxMap) world = this.SetFast Property? TmxMap false value world
        member this.TmxMap = lens Property? TmxMap this.GetTmxMap this.SetTmxMap this

    type TmxMapFacet () =
        inherit Facet ()

        static member Properties =
            [define Entity.Omnipresent true
             define Entity.PublishChanges true
             define Entity.BodyEnabled true
             define Entity.Friction 0.0f
             define Entity.Restitution 0.0f
             define Entity.CollisionCategories "1"
             define Entity.CollisionMask "@"
             define Entity.TmxMap (TmxMap Assets.Default.TmxFilePath)
             define Entity.TileLayerClearance 2.0f
             define Entity.Parallax 0.0f
             computed Entity.PhysicsId (fun (entity : Entity) world -> { SourceId = entity.GetId world; CorrelationId = Gen.idEmpty }) None]

        override this.Register (entity, world) =
            let world = entity.SetSize (entity.GetQuickSize world) world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? BodyEnabled) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? Transform) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? Friction) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? Restitution) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? CollisionCategories) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? CollisionMask) entity world
            let world = World.monitor (fun _ world -> (Cascade, entity.PropagatePhysics world)) (entity.ChangeEvent Property? TmxMap) entity world
            let world =
                World.monitor (fun _ world ->
                    let quickSize = entity.GetQuickSize world
                    let transform = entity.GetTransform world
                    let world = entity.SetTransformWithoutEvent { transform with Size = quickSize } world
                    (Cascade, entity.PropagatePhysics world))
                    (entity.ChangeEvent Property? TmxMap)
                    entity
                    world
            world

        override this.RegisterPhysics (entity, world) =
            let tileMapPosition = entity.GetPosition world
            let tileMap = entity.GetTmxMap world
            match TmxMap.tryGetDescriptor tileMapPosition tileMap with
            | Some tileMapDescriptor ->
                let bodyProperties =
                    TmxMap.getBodyProperties
                        (entity.GetEnabled world)
                        (entity.GetFriction world)
                        (entity.GetRestitution world)
                        (entity.GetCollisionCategories world)
                        (entity.GetCollisionMask world)
                        (entity.GetPhysicsId world).CorrelationId
                        tileMapDescriptor
                World.createBody entity (entity.GetId world) bodyProperties world
            | None -> world

        override this.UnregisterPhysics (entity, world) =
            World.destroyBody (entity.GetPhysicsId world) world

        override this.Actualize (entity, world) =
            if entity.GetVisible world then
                let absolute = entity.GetAbsolute world
                let viewBounds = World.getViewBounds absolute world
                let tileMapRenderMessages =
                    TmxMap.getRenderMessages
                        absolute
                        viewBounds
                        (entity.GetTileLayerClearance world)
                        (entity.GetPosition world)
                        (entity.GetElevation world)
                        (entity.GetParallax world)
                        (entity.GetTmxMap world)
                World.enqueueRenderMessages tileMapRenderMessages world
            else world

        override this.GetQuickSize (entity, world) =
            TmxMap.getQuickSize (entity.GetTmxMap world)

[<AutoOpen>]
module NodeFacetModule =

    type Entity with

        member this.GetParentNodeOpt world : Entity Relation option = this.Get Property? ParentNodeOpt world
        member this.SetParentNodeOpt (value : Entity Relation option) world = this.SetFast Property? ParentNodeOpt true value world
        member this.ParentNodeOpt = lens Property? ParentNodeOpt this.GetParentNodeOpt this.SetParentNodeOpt this
        member this.GetPositionLocal world : Vector2 = this.Get Property? PositionLocal world
        member this.SetPositionLocal (value : Vector2) world = this.SetFast Property? PositionLocal false value world
        member this.PositionLocal = lens Property? PositionLocal this.GetPositionLocal this.SetPositionLocal this
        member this.GetCenterLocal world : Vector2 = this.Get Property? CenterLocal world
        member this.SetCenterLocal (value : Vector2) world = this.SetFast Property? CenterLocal false value world
        member this.CenterLocal = lens Property? CenterLocal this.GetCenterLocal this.SetCenterLocal this
        member this.GetBottomLocal world : Vector2 = this.Get Property? BottomLocal world
        member this.SetBottomLocal (value : Vector2) world = this.SetFast Property? BottomLocal false value world
        member this.BottomLocal = lens Property? BottomLocal this.GetBottomLocal this.SetBottomLocal this
        member this.GetElevationLocal world : single = this.Get Property? ElevationLocal world
        member this.SetElevationLocal (value : single) world = this.SetFast Property? ElevationLocal false value world
        member this.ElevationLocal = lens Property? ElevationLocal this.GetElevationLocal this.SetElevationLocal this
        member this.GetVisibleLocal world : bool = this.Get Property? VisibleLocal world
        member this.SetVisibleLocal (value : bool) world = this.SetFast Property? VisibleLocal false value world
        member this.VisibleLocal = lens Property? VisibleLocal this.GetVisibleLocal this.SetVisibleLocal this
        member this.GetEnabledLocal world : bool = this.Get Property? EnabledLocal world
        member this.SetEnabledLocal (value : bool) world = this.SetFast Property? EnabledLocal false value world
        member this.EnabledLocal = lens Property? EnabledLocal this.GetEnabledLocal this.SetEnabledLocal this
        member private this.GetNodeUnsubscribe world : World -> World = this.Get Property? NodeUnsubscribe world
        member private this.SetNodeUnsubscribe (value : World -> World) world = this.SetFast Property? NodeUnsubscribe false value world
        member private this.NodeUnsubscribe = lens Property? NodeUnsubscribe this.GetNodeUnsubscribe this.SetNodeUnsubscribe this

        member this.SetParentNodeOptWithAdjustment (value : Entity Relation option) world =
            let world =
                match (this.GetParentNodeOpt world, value) with
                | (Some relationOld, Some relationNew) ->
                    let parentOld = this.Resolve relationOld
                    let parentNew = this.Resolve relationNew
                    if parentOld.Exists world && parentNew.Exists world then
                        let position = this.GetPositionLocal world + parentNew.GetPosition world
                        let elevation = this.GetElevationLocal world + parentNew.GetElevation world
                        let world = this.SetPosition position world
                        let world = this.SetElevation elevation world
                        let world = this.SetVisible (this.GetVisibleLocal world && parentNew.GetVisible world) world
                        let world = this.SetEnabled (this.GetEnabledLocal world && parentNew.GetEnabled world) world
                        world
                    else world
                | (Some relationOld, None) ->
                    let parentOld = this.Resolve relationOld
                    if parentOld.Exists world then
                        let position = this.GetPositionLocal world + parentOld.GetPosition world
                        let elevation = this.GetElevationLocal world + parentOld.GetElevation world
                        let world = this.SetPosition position world
                        let world = this.SetElevation elevation world
                        let world = this.SetVisible (this.GetVisibleLocal world) world
                        let world = this.SetEnabled (this.GetEnabledLocal world) world
                        world
                    else world
                | (None, Some relationNew) ->
                    let parentNew = this.Resolve relationNew
                    if parentNew.Exists world then
                        let position = this.GetPosition world - parentNew.GetPosition world
                        let elevation = this.GetElevation world - parentNew.GetElevation world
                        let world = this.SetPositionLocal position world
                        let world = this.SetElevationLocal elevation world
                        let world = this.SetVisible (this.GetVisibleLocal world && parentNew.GetVisible world) world
                        let world = this.SetEnabled (this.GetEnabledLocal world && parentNew.GetEnabled world) world
                        world
                    else world
                | (None, None) -> world
            this.SetParentNodeOpt value world

        member this.GetChildNodes world =
            this.GetChildNodes2 [] world

        member this.ParentNodeExists world =
            match this.GetParentNodeOpt world with
            | Some relation -> (this.Resolve relation).Exists world
            | None -> false

        member private this.GetChildNodes2 nodes world =
            let nodeOpt =
                if this.Has<NodeFacet> world
                then Option.map this.Resolve (this.GetParentNodeOpt world)
                else None
            match nodeOpt with
            | Some node -> node.GetChildNodes2 (node :: nodes) world
            | None -> nodes

    and NodeFacet () =
        inherit Facet ()

        static let updatePropertyFromLocal3 propertyName (entity : Entity) world =
            match propertyName with
            | "Position" -> entity.SetPosition (entity.GetPositionLocal world) world
            | "Elevation" -> entity.SetElevation (entity.GetElevationLocal world) world
            | "Visible" -> entity.SetVisible (entity.GetVisibleLocal world) world
            | "Enabled" -> entity.SetEnabled (entity.GetEnabledLocal world) world
            | _ -> world

        static let updatePropertyFromLocal propertyName (node : Entity) (entity : Entity) world =
            if node.Exists world then
                match propertyName with
                | "PositionLocal" -> entity.SetPosition (node.GetPosition world + entity.GetPositionLocal world) world
                | "ElevationLocal" -> entity.SetElevation (node.GetElevation world + entity.GetElevationLocal world) world
                | "VisibleLocal" -> entity.SetVisible (node.GetVisible world && entity.GetVisibleLocal world) world
                | "EnabledLocal" -> entity.SetEnabled (node.GetEnabled world && entity.GetEnabledLocal world) world
                | _ -> world
            else world

        static let updatePropertyFromNode propertyName (node : Entity) (entity : Entity) world =
            if node.Exists world then
                match propertyName with
                | "Position" -> entity.SetPosition (node.GetPosition world + entity.GetPositionLocal world) world
                | "Elevation" -> entity.SetElevation (node.GetElevation world + entity.GetElevationLocal world) world
                | "Visible" -> entity.SetVisible (node.GetVisible world && entity.GetVisibleLocal world) world
                | "Enabled" -> entity.SetEnabled (node.GetEnabled world && entity.GetEnabledLocal world) world
                | _ -> world
            else world

        static let updateFromNode (node : Entity) (entity : Entity) world =
            let world = updatePropertyFromNode "Position" node entity world
            let world = updatePropertyFromNode "Elevation" node entity world
            let world = updatePropertyFromNode "Visible" node entity world
            let world = updatePropertyFromNode "Enabled" node entity world
            world

        static let tryUpdateFromNode (entity : Entity) world =
            match entity.GetParentNodeOpt world with
            | Some nodeRelation ->
                let node = entity.Resolve nodeRelation
                let world = updateFromNode node entity world
                world
            | None -> world

        static let handleLocalPropertyChange evt world =
            let entity = evt.Subscriber : Entity
            let data = evt.Data : ChangeData
            match entity.GetParentNodeOpt world with
            | Some relation ->
                let node = entity.Resolve relation
                if World.getEntityExists node world
                then (Cascade, updatePropertyFromLocal data.Name node entity world)
                else (Cascade, updatePropertyFromLocal3 data.Name entity world)
            | None -> (Cascade, updatePropertyFromLocal3 data.Name entity world)

        static let handleNodePropertyChange evt world =
            let entity = evt.Subscriber : Entity
            let node = evt.Publisher :?> Entity
            let data = evt.Data : ChangeData
            (Cascade, updatePropertyFromNode data.Name node entity world)

        static let trySubscribeToNodePropertyChanges (entity : Entity) world =
            let oldWorld = world
            let world = (entity.GetNodeUnsubscribe world) world
            match entity.GetParentNodeOpt world with
            | Some nodeRelation ->
                let node = entity.Resolve nodeRelation
                if node = entity then
                    Log.trace "Cannot mount entity to itself."
                    World.choose oldWorld
                elif entity.Has<RigidBodyFacet> world then
                    Log.trace "Cannot mount a rigid body entity onto another entity. Instead, consider using physics constraints."
                    World.choose oldWorld
                else
                    let (unsubscribe, world) = World.monitorPlus handleNodePropertyChange node.Position.ChangeEvent entity world
                    let (unsubscribe2, world) = World.monitorPlus handleNodePropertyChange node.Elevation.ChangeEvent entity world
                    let (unsubscribe3, world) = World.monitorPlus handleNodePropertyChange node.Visible.ChangeEvent entity world
                    let (unsubscribe4, world) = World.monitorPlus handleNodePropertyChange node.Enabled.ChangeEvent entity world
                    entity.SetNodeUnsubscribe (unsubscribe4 >> unsubscribe3 >> unsubscribe2 >> unsubscribe) world
            | None -> world

        static let handleNodeChange evt world =
            let entity = evt.Subscriber
            let world = tryUpdateFromNode entity world
            let world = trySubscribeToNodePropertyChanges entity world
            (Cascade, world)

        static member Properties =
            [define Entity.ParentNodeOpt None
             define Entity.PositionLocal Vector2.Zero
             define Entity.ElevationLocal 0.0f
             define Entity.VisibleLocal true
             define Entity.EnabledLocal true
             define Entity.NodeUnsubscribe (id : World -> World)
             computed Entity.CenterLocal
                (fun (entity : Entity) world -> entity.GetPositionLocal world + entity.GetSize world * 0.5f)
                (Some (fun value (entity : Entity) world -> entity.SetPositionLocal (value - entity.GetSize world * 0.5f) world))
             computed Entity.BottomLocal
                (fun (entity : Entity) world -> entity.GetPositionLocal world + (entity.GetSize world).WithY 0.0f * 0.5f)
                (Some (fun value (entity : Entity) world -> entity.SetPositionLocal (value - (entity.GetSize world).WithY 0.0f * 0.5f) world))]

        override this.Register (entity, world) =
            let world = entity.SetNodeUnsubscribe id world // ensure unsubscribe function reference doesn't get copied in Gaia...
            let world = World.monitor handleNodeChange entity.ParentNodeOpt.ChangeEvent entity world
            let world = World.monitorPlus handleLocalPropertyChange entity.PositionLocal.ChangeEvent entity world |> snd
            let world = World.monitorPlus handleLocalPropertyChange entity.ElevationLocal.ChangeEvent entity world |> snd
            let world = World.monitorPlus handleLocalPropertyChange entity.VisibleLocal.ChangeEvent entity world |> snd
            let world = World.monitorPlus handleLocalPropertyChange entity.EnabledLocal.ChangeEvent entity world |> snd
            let world = tryUpdateFromNode entity world
            let world = trySubscribeToNodePropertyChanges entity world
            world

        override this.Unregister (entity, world) =
            (entity.GetNodeUnsubscribe world) world // NOTE: not sure if this is necessary.

[<AutoOpen>]
module StaticSpriteFacetModule =

    type Entity with
        member this.GetStaticImage world : Image AssetTag = this.Get Property? StaticImage world
        member this.SetStaticImage (value : Image AssetTag) world = this.SetFast Property? StaticImage false value world
        member this.StaticImage = lens Property? StaticImage this.GetStaticImage this.SetStaticImage this
        member this.GetColor world : Color = this.Get Property? Color world
        member this.SetColor (value : Color) world = this.SetFast Property? Color false value world
        member this.Color = lens Property? Color this.GetColor this.SetColor this
        member this.GetGlow world : Color = this.Get Property? Glow world
        member this.SetGlow (value : Color) world = this.SetFast Property? Glow false value world
        member this.Glow = lens Property? Glow this.GetGlow this.SetGlow this
        member this.GetInsetOpt world : Vector4 option = this.Get Property? Inset world
        member this.SetInsetOpt (value : Vector4 option) world = this.SetFast Property? Inset false value world
        member this.InsetOpt = lens Property? Inset this.GetInsetOpt this.SetInsetOpt this
        member this.GetFlip world : Flip = this.Get Property? Flip world
        member this.SetFlip (value : Flip) world = this.SetFast Property? Flip false value world
        member this.Flip = lens Property? Flip this.GetFlip this.SetFlip this

    type StaticSpriteFacet () =
        inherit Facet ()

        static member Properties =
            [define Entity.StaticImage Assets.Default.Image4
             define Entity.Color Color.White
             define Entity.Glow Color.Zero
             define Entity.InsetOpt None
             define Entity.Flip FlipNone]

        override this.Actualize (entity, world) =
            if entity.GetVisible world && entity.GetInView world then
                let transform = entity.GetTransform world
                World.enqueueRenderMessage
                    (LayeredDescriptorMessage
                        { Elevation = transform.Elevation
                          PositionY = transform.Position.Y
                          AssetTag = entity.GetStaticImage world |> AssetTag.generalize
                          RenderDescriptor =
                            SpriteDescriptor
                                { Transform = transform
                                  Offset = Vector2.Zero
                                  InsetOpt = entity.GetInsetOpt world
                                  Image = entity.GetStaticImage world
                                  Color = entity.GetColor world
                                  Glow = entity.GetGlow world
                                  Flip = entity.GetFlip world }})
                    world
            else world

        override this.GetQuickSize (entity, world) =
            match World.tryGetTextureSizeF (entity.GetStaticImage world) world with
            | Some size -> size
            | None -> Constants.Engine.EntitySizeDefault

[<AutoOpen>]
module AnimatedSpriteFacetModule =

    type Entity with
        member this.GetCelSize world : Vector2 = this.Get Property? CelSize world
        member this.SetCelSize (value : Vector2) world = this.SetFast Property? CelSize false value world
        member this.CelSize = lens Property? CelSize this.GetCelSize this.SetCelSize this
        member this.GetCelRun world : int = this.Get Property? CelRun world
        member this.SetCelRun (value : int) world = this.SetFast Property? CelRun false value world
        member this.CelRun = lens Property? CelRun this.GetCelRun this.SetCelRun this
        member this.GetCelCount world : int = this.Get Property? CelCount world
        member this.SetCelCount (value : int) world = this.SetFast Property? CelCount false value world
        member this.CelCount = lens Property? CelCount this.GetCelCount this.SetCelCount this
        member this.GetAnimationDelay world : int64 = this.Get Property? AnimationDelay world
        member this.SetAnimationDelay (value : int64) world = this.SetFast Property? AnimationDelay false value world
        member this.AnimationDelay = lens Property? AnimationDelay this.GetAnimationDelay this.SetAnimationDelay this
        member this.GetAnimationSheet world : Image AssetTag = this.Get Property? AnimationSheet world
        member this.SetAnimationSheet (value : Image AssetTag) world = this.SetFast Property? AnimationSheet false value world
        member this.AnimationSheet = lens Property? AnimationSheet this.GetAnimationSheet this.SetAnimationSheet this

    type AnimatedSpriteFacet () =
        inherit Facet ()

        static let getSpriteInsetOpt (entity : Entity) world =
            let celCount = entity.GetCelCount world
            let celRun = entity.GetCelRun world
            if celCount <> 0 && celRun <> 0 then
                let cel = int (World.getTickTime world / entity.GetAnimationDelay world) % celCount
                let celSize = entity.GetCelSize world
                let celI = cel % celRun
                let celJ = cel / celRun
                let celX = single celI * celSize.X
                let celY = single celJ * celSize.Y
                let inset = v4Bounds (v2 celX celY) celSize
                Some inset
            else None

        static member Properties =
            [define Entity.CelSize (Vector2 (16.0f, 16.0f))
             define Entity.CelRun 4
             define Entity.CelCount 16
             define Entity.AnimationDelay 4L
             define Entity.AnimationSheet Assets.Default.Image7
             define Entity.Color Color.White
             define Entity.Glow Color.Zero
             define Entity.Flip FlipNone]

        override this.Actualize (entity, world) =
            if entity.GetVisible world && entity.GetInView world then
                let transform = entity.GetTransform world
                World.enqueueRenderMessage
                    (LayeredDescriptorMessage
                        { Elevation = transform.Elevation
                          PositionY = transform.Position.Y
                          AssetTag = entity.GetAnimationSheet world |> AssetTag.generalize
                          RenderDescriptor =
                            SpriteDescriptor
                                { Transform = transform
                                  Offset = Vector2.Zero
                                  InsetOpt = getSpriteInsetOpt entity world
                                  Image = entity.GetAnimationSheet world
                                  Color = entity.GetColor world
                                  Glow = entity.GetGlow world
                                  Flip = entity.GetFlip world }})
                    world
            else world

        override this.GetQuickSize (entity, world) =
            entity.GetCelSize world

[<AutoOpen>]
module EntityDispatcherModule =

    type World with

        static member internal signalEntity<'model, 'message, 'command> signal (entity : Entity) world =
            match entity.GetDispatcher world with
            | :? EntityDispatcher<'model, 'message, 'command> as dispatcher ->
                Signal.processSignal dispatcher.Message dispatcher.Command (entity.Model<'model> ()) signal entity world
            | _ ->
                Log.info "Failed to send signal to entity."
                world

    and Entity with

        member this.UpdateModel<'model> updater world =
            this.SetModel<'model> (updater (this.GetModel<'model> world)) world

        member this.Signal<'model, 'message, 'command> signal world =
            World.signalEntity<'model, 'message, 'command> signal this world

    and [<AbstractClass>] EntityDispatcher<'model, 'message, 'command> (initial : 'model) =
        inherit EntityDispatcher ()

        member this.GetModel (entity : Entity) world : 'model =
            entity.GetModel<'model> world

        member this.SetModel (model : 'model) (entity : Entity) world =
            entity.SetModel<'model> model world

        member this.Model (entity : Entity) =
            lens Property? Model (this.GetModel entity) (flip this.SetModel entity) entity

        override this.Register (entity, world) =
            let world =
                let property = World.getEntityModelProperty entity world
                if property.DesignerType = typeof<unit>
                then entity.SetModel<'model> initial world
                else world
            let channels = this.Channel (this.Model entity, entity)
            let world = Signal.processChannels this.Message this.Command (this.Model entity) channels entity world
            let content = this.Content (this.Model entity, entity)
            let world =
                List.fold (fun world content ->
                    World.expandEntityContent content (SimulantOrigin entity) entity entity.Parent world |> snd)
                    world content
            let initializers = this.Initializers (this.Model entity, entity)
            List.fold (fun world initializer ->
                match initializer with
                | PropertyDefinition def ->
                    let propertyName = def.PropertyName
                    let alwaysPublish = Reflection.isPropertyAlwaysPublishByName propertyName
                    let property = { PropertyType = def.PropertyType; PropertyValue = PropertyExpr.eval def.PropertyExpr world }
                    World.setProperty def.PropertyName alwaysPublish property entity world
                | EventHandlerDefinition (handler, partialAddress) ->
                    let eventAddress = partialAddress --> entity
                    World.monitor (fun (evt : Event) world ->
                        let world = WorldModule.trySignal (handler evt) entity world
                        (Cascade, world))
                        eventAddress (entity :> Simulant) world
                | BindDefinition (left, right) ->
                    WorldModule.bind5 entity left right world)
                world initializers

        override this.Actualize (entity, world) =
            let view = this.View (this.GetModel entity world, entity, world)
            World.actualizeView view world

        override this.TrySignalFacet (signalObj : obj, facetName : string, entity : Entity, world : World) : World =
            entity.TrySignalFacet signalObj facetName world

        override this.TrySignal (signalObj, entity, world) =
            match signalObj with
            | :? Signal<'message, obj> as signal -> entity.Signal<'model, 'message, 'command> (match signal with Message message -> msg message | _ -> failwithumf ()) world
            | :? Signal<obj, 'command> as signal -> entity.Signal<'model, 'message, 'command> (match signal with Command command -> cmd command | _ -> failwithumf ()) world
            | _ -> Log.info "Incorrect signal type returned from event binding."; world

        abstract member Initializers : Lens<'model, World> * Entity -> PropertyInitializer list
        default this.Initializers (_, _) = []

        abstract member Channel : Lens<'model, World> * Entity -> Channel<'message, 'command, Entity, World> list
        default this.Channel (_, _) = []

        abstract member Message : 'model * 'message * Entity * World -> Signal<'message, 'command> list * 'model
        default this.Message (model, _, _, _) = just model

        abstract member Command : 'model * 'command * Entity * World -> Signal<'message, 'command> list * World
        default this.Command (_, _, _, world) = just world

        abstract member Content : Lens<'model, World> * Entity -> EntityContent list
        default this.Content (_, _) = []

        abstract member View : 'model * Entity * World -> View
        default this.View (_, _, _) = View.empty

[<AutoOpen>]
module EffectDispatcherModule =

    type EffectDispatcher () =
        inherit EntityDispatcher ()

        static member Facets =
            [typeof<EffectFacet>]

        static member Properties =
            [define Entity.PublishChanges true
             define Entity.Effect (scvalue<Effect> "[Effect None [] [Contents [Shift 0] [[StaticSprite [Resource Default Image] FlipNone [] Nil]]]]")]

[<AutoOpen>]
module StaticSpriteDispatcherModule =

    type StaticSpriteDispatcher () =
        inherit EntityDispatcher ()

        static member Facets =
            [typeof<StaticSpriteFacet>]

        static member Properties =
            [define Entity.StaticImage Assets.Default.Image4
             define Entity.Color Color.White
             define Entity.Glow Color.Zero
             define Entity.InsetOpt None
             define Entity.Flip FlipNone]

[<AutoOpen>]
module AnimatedSpriteDispatcherModule =

    type AnimatedSpriteDispatcher () =
        inherit EntityDispatcher ()

        static member Facets =
            [typeof<AnimatedSpriteFacet>]

        static member Properties =
            [define Entity.CelSize (Vector2 (16.0f, 16.0f))
             define Entity.CelRun 4
             define Entity.CelCount 16
             define Entity.AnimationDelay 4L
             define Entity.AnimationSheet Assets.Default.Image7
             define Entity.Color Color.White
             define Entity.Glow Color.Zero
             define Entity.Flip FlipNone]

[<AutoOpen>]
module NodeDispatcherModule =

    type NodeDispatcher () =
        inherit EntityDispatcher ()

        static member Facets =
            [typeof<NodeFacet>]

        static member Properties =
            [define Entity.PublishChanges true]

    type [<AbstractClass>] NodeDispatcher<'model, 'message, 'command> (model) =
        inherit EntityDispatcher<'model, 'message, 'command> (model)

        static member Facets =
            [typeof<NodeFacet>]

        static member Properties =
            [define Entity.PublishChanges true]

[<AutoOpen>]
module GuiDispatcherModule =

    type Entity with
        member this.GetDisabledColor world : Color = this.Get Property? DisabledColor world
        member this.SetDisabledColor (value : Color) world = this.SetFast Property? DisabledColor false value world
        member this.DisabledColor = lens Property? DisabledColor this.GetDisabledColor this.SetDisabledColor this
        member this.GetSwallowMouseLeft world : bool = this.Get Property? SwallowMouseLeft world
        member this.SetSwallowMouseLeft (value : bool) world = this.SetFast Property? SwallowMouseLeft false value world
        member this.SwallowMouseLeft = lens Property? SwallowMouseLeft this.GetSwallowMouseLeft this.SetSwallowMouseLeft this

    type GuiDispatcher () =
        inherit EntityDispatcher ()

        static let handleMouseLeft evt world =
            let entity = evt.Subscriber : Entity
            let data = evt.Data : MouseButtonData
            let handling =
                if entity.GetSelected world && entity.GetVisible world then
                    let mousePositionWorld = World.mouseToWorld (entity.GetAbsolute world) data.Position world
                    if data.Down &&
                       entity.GetSwallowMouseLeft world &&
                       Math.isPointInBounds mousePositionWorld (entity.GetBounds world) then
                       Resolve
                    else Cascade
                else Cascade
            (handling, world)

        static member Facets =
            [typeof<NodeFacet>]

        static member Properties =
            [define Entity.Omnipresent true
             define Entity.Absolute true
             define Entity.PublishChanges true
             define Entity.AlwaysUpdate true
             define Entity.DisabledColor (Color (byte 192, byte 192, byte 192, byte 192))
             define Entity.SwallowMouseLeft true]

        override this.Register (entity, world) =
            let world = World.monitor handleMouseLeft Events.MouseLeftDown entity world
            let world = World.monitor handleMouseLeft Events.MouseLeftUp entity world
            world

    type [<AbstractClass>] GuiDispatcher<'model, 'message, 'command> (model) =
        inherit EntityDispatcher<'model, 'message, 'command> (model)

        static let handleMouseLeft evt world =
            let entity = evt.Subscriber : Entity
            let data = evt.Data : MouseButtonData
            let handling =
                if entity.GetSelected world && entity.GetVisible world then
                    let mousePositionWorld = World.mouseToWorld (entity.GetAbsolute world) data.Position world
                    if data.Down &&
                       entity.GetSwallowMouseLeft world &&
                       Math.isPointInBounds mousePositionWorld (entity.GetBounds world) then
                       Resolve
                    else Cascade
                else Cascade
            (handling, world)

        static member Facets =
            [typeof<NodeFacet>]

        static member Properties =
            [define Entity.Absolute true
             define Entity.PublishChanges true
             define Entity.AlwaysUpdate true
             define Entity.DisabledColor (Color (byte 192, byte 192, byte 192, byte 192))
             define Entity.SwallowMouseLeft true]

        override this.Register (entity, world) =
            let world = base.Register (entity, world)
            let world = World.monitor handleMouseLeft Events.MouseLeftDown entity world
            let world = World.monitor handleMouseLeft Events.MouseLeftUp entity world
            world

[<AutoOpen>]
module ButtonDispatcherModule =

    type Entity with
        member this.GetDown world : bool = this.Get Property? Down world
        member this.SetDown (value : bool) world = this.SetFast Property? Down false value world
        member this.Down = lens Property? Down this.GetDown this.SetDown this
        member this.GetUpImage world : Image AssetTag = this.Get Property? UpImage world
        member this.SetUpImage (value : Image AssetTag) world = this.SetFast Property? UpImage false value world
        member this.UpImage = lens Property? UpImage this.GetUpImage this.SetUpImage this
        member this.GetDownImage world : Image AssetTag = this.Get Property? DownImage world
        member this.SetDownImage (value : Image AssetTag) world = this.SetFast Property? DownImage false value world
        member this.DownImage = lens Property? DownImage this.GetDownImage this.SetDownImage this
        member this.GetClickSoundOpt world : Sound AssetTag option = this.Get Property? ClickSoundOpt world
        member this.SetClickSoundOpt (value : Sound AssetTag option) world = this.SetFast Property? ClickSoundOpt false value world
        member this.ClickSoundOpt = lens Property? ClickSoundOpt this.GetClickSoundOpt this.SetClickSoundOpt this
        member this.GetClickSoundVolume world : single = this.Get Property? ClickSoundVolume world
        member this.SetClickSoundVolume (value : single) world = this.SetFast Property? ClickSoundVolume false value world
        member this.ClickSoundVolume = lens Property? ClickSoundVolume this.GetClickSoundVolume this.SetClickSoundVolume this
        member this.UpEvent = Events.Up --> this
        member this.DownEvent = Events.Down --> this
        member this.ClickEvent = Events.Click --> this

    type ButtonDispatcher () =
        inherit GuiDispatcher ()

        let handleMouseLeftDown evt world =
            let entity = evt.Subscriber : Entity
            let data = evt.Data : MouseButtonData
            if entity.GetSelected world then
                let mousePositionWorld = World.mouseToWorld (entity.GetAbsolute world) data.Position world
                if  entity.GetVisible world &&
                    Math.isPointInBounds mousePositionWorld (entity.GetBounds world) then
                    if entity.GetEnabled world then
                        let world = entity.SetDown true world
                        let eventTrace = EventTrace.debug "ButtonDispatcher" "handleMouseLeftDown" EventTrace.empty
                        let world = World.publishPlus () (Events.Down --> entity) eventTrace entity true world
                        (Resolve, world)
                    else (Resolve, world)
                else (Cascade, world)
            else (Cascade, world)

        let handleMouseLeftUp evt world =
            let entity = evt.Subscriber : Entity
            let data = evt.Data : MouseButtonData
            if entity.GetSelected world then
                let wasDown = entity.GetDown world
                let world = entity.SetDown false world
                let mousePositionWorld = World.mouseToWorld (entity.GetAbsolute world) data.Position world
                if  entity.GetVisible world &&
                    Math.isPointInBounds mousePositionWorld (entity.GetBounds world) then
                    if entity.GetEnabled world && wasDown then
                        let eventTrace = EventTrace.record4 "ButtonDispatcher" "handleMouseLeftUp" "Up" EventTrace.empty
                        let world = World.publishPlus () (Events.Up --> entity) eventTrace entity true world
                        let eventTrace = EventTrace.record4 "ButtonDispatcher" "handleMouseLeftUp" "Click" EventTrace.empty
                        let world = World.publishPlus () (Events.Click --> entity) eventTrace entity true world
                        let world =
                            match entity.GetClickSoundOpt world with
                            | Some clickSound -> World.playSound (entity.GetClickSoundVolume world) clickSound world
                            | None -> world
                        (Resolve, world)
                    else (Resolve, world)
                else (Cascade, world)
            else (Cascade, world)

        static member Facets =
            [typeof<TextFacet>]

        static member Properties =
            [define Entity.Size (Vector2 (192.0f, 48.0f))
             define Entity.SwallowMouseLeft false
             define Entity.Down false
             define Entity.UpImage Assets.Default.Image
             define Entity.DownImage Assets.Default.Image2
             define Entity.ClickSoundOpt (Some Assets.Default.Sound)
             define Entity.ClickSoundVolume Constants.Audio.SoundVolumeDefault]

        override this.Register (entity, world) =
            let world = World.monitor handleMouseLeftDown Events.MouseLeftDown entity world
            let world = World.monitor handleMouseLeftUp Events.MouseLeftUp entity world
            world

        override this.Actualize (entity, world) =
            if entity.GetVisible world then
                let transform = entity.GetTransform world
                let image = if entity.GetDown world then entity.GetDownImage world else entity.GetUpImage world
                World.enqueueRenderMessage
                    (LayeredDescriptorMessage
                        { Elevation = transform.Elevation
                          PositionY = transform.Position.Y
                          AssetTag = AssetTag.generalize image
                          RenderDescriptor =
                            SpriteDescriptor
                                { Transform = transform
                                  Offset = Vector2.Zero
                                  InsetOpt = None
                                  Image = image
                                  Color = if entity.GetEnabled world then Color.White else entity.GetDisabledColor world
                                  Glow = Color.Zero
                                  Flip = FlipNone }})
                    world
            else world

        override this.GetQuickSize (entity, world) =
            match World.tryGetTextureSizeF (entity.GetUpImage world) world with
            | Some size -> size
            | None -> Constants.Engine.EntitySizeDefault

[<AutoOpen>]
module LabelDispatcherModule =

    type Entity with
        member this.GetLabelImage world : Image AssetTag = this.Get Property? LabelImage world
        member this.SetLabelImage (value : Image AssetTag) world = this.SetFast Property? LabelImage false value world
        member this.LabelImage = lens Property? LabelImage this.GetLabelImage this.SetLabelImage this

    type LabelDispatcher () =
        inherit GuiDispatcher ()

        static member Properties =
            [define Entity.Size (Vector2 (192.0f, 48.0f))
             define Entity.SwallowMouseLeft false
             define Entity.LabelImage Assets.Default.Image3]

        override this.Actualize (entity, world) =
            if entity.GetVisible world then
                let transform = entity.GetTransform world
                World.enqueueRenderMessage
                    (LayeredDescriptorMessage
                        { Elevation = transform.Elevation
                          PositionY = transform.Position.Y
                          AssetTag = entity.GetLabelImage world |> AssetTag.generalize
                          RenderDescriptor =
                            SpriteDescriptor
                                { Transform = transform
                                  Offset = Vector2.Zero
                                  InsetOpt = None
                                  Image = entity.GetLabelImage world
                                  Color = if entity.GetEnabled world then Color.White else entity.GetDisabledColor world
                                  Glow = Color.Zero
                                  Flip = FlipNone }})
                    world
            else world

        override this.GetQuickSize (entity, world) =
            match World.tryGetTextureSizeF (entity.GetLabelImage world) world with
            | Some size -> size
            | None -> Constants.Engine.EntitySizeDefault

[<AutoOpen>]
module TextDispatcherModule =

    type Entity with
        member this.GetBackgroundImageOpt world : Image AssetTag option = this.Get Property? BackgroundImageOpt world
        member this.SetBackgroundImageOpt (value : Image AssetTag option) world = this.SetFast Property? BackgroundImageOpt false value world
        member this.BackgroundImageOpt = lens Property? BackgroundImageOpt this.GetBackgroundImageOpt this.SetBackgroundImageOpt this

    type TextDispatcher () =
        inherit GuiDispatcher ()

        static member Facets =
            [typeof<TextFacet>]

        static member Properties =
            [define Entity.Size (Vector2 (192.0f, 48.0f))
             define Entity.SwallowMouseLeft false
             define Entity.BackgroundImageOpt None
             define Entity.Justification (Justified (JustifyLeft, JustifyMiddle))]

        override this.Register (entity, world) =
            let world = bind (entity.Model<string> ()) entity.Text world
            let world = bind entity.Text (entity.Model<string> ()) world
            world

        override this.Actualize (entity, world) =
            if entity.GetVisible world then
                match entity.GetBackgroundImageOpt world with
                | Some image ->
                    let transform = entity.GetTransform world
                    World.enqueueRenderMessage
                        (LayeredDescriptorMessage
                            { Elevation = transform.Elevation
                              PositionY = transform.Position.Y
                              AssetTag = AssetTag.generalize image
                              RenderDescriptor =
                                SpriteDescriptor
                                    { Transform = transform
                                      Offset = Vector2.Zero
                                      InsetOpt = None
                                      Image = image
                                      Color = if entity.GetEnabled world then Color.White else entity.GetDisabledColor world
                                      Glow = Color.Zero
                                      Flip = FlipNone }})
                        world
                | None -> world
            else world

        override this.GetQuickSize (entity, world) =
            match entity.GetBackgroundImageOpt world with
            | Some image ->
                match World.tryGetTextureSizeF image world with
                | Some size -> size
                | None -> Constants.Engine.EntitySizeDefault
            | None -> Constants.Engine.EntitySizeDefault

[<AutoOpen>]
module ToggleDispatcherModule =

    type Entity with
        member this.GetOpen world : bool = this.Get Property? Open world
        member this.SetOpen (value : bool) world = this.SetFast Property? Open false value world
        member this.Open = lens Property? Open this.GetOpen this.SetOpen this
        member this.GetPressed world : bool = this.Get Property? Pressed world
        member this.SetPressed (value : bool) world = this.SetFast Property? Pressed false value world
        member this.Pressed = lens Property? Pressed this.GetPressed this.SetPressed this
        member this.GetOpenImage world : Image AssetTag = this.Get Property? OpenImage world
        member this.SetOpenImage (value : Image AssetTag) world = this.SetFast Property? OpenImage false value world
        member this.OpenImage = lens Property? OpenImage this.GetOpenImage this.SetOpenImage this
        member this.GetClosedImage world : Image AssetTag = this.Get Property? ClosedImage world
        member this.SetClosedImage (value : Image AssetTag) world = this.SetFast Property? ClosedImage false value world
        member this.ClosedImage = lens Property? ClosedImage this.GetClosedImage this.SetClosedImage this
        member this.GetToggleSoundOpt world : Sound AssetTag option = this.Get Property? ToggleSoundOpt world
        member this.SetToggleSoundOpt (value : Sound AssetTag option) world = this.SetFast Property? ToggleSoundOpt false value world
        member this.ToggleSoundOpt = lens Property? ToggleSoundOpt this.GetToggleSoundOpt this.SetToggleSoundOpt this
        member this.GetToggleSoundVolume world : single = this.Get Property? ToggleSoundVolume world
        member this.SetToggleSoundVolume (value : single) world = this.SetFast Property? ToggleSoundVolume false value world
        member this.ToggleSoundVolume = lens Property? ToggleSoundVolume this.GetToggleSoundVolume this.SetToggleSoundVolume this
        member this.ToggleEvent = Events.Toggle --> this

    type ToggleDispatcher () =
        inherit GuiDispatcher ()
        
        let handleMouseLeftDown evt world =
            let entity = evt.Subscriber : Entity
            let data = evt.Data : MouseButtonData
            if entity.GetSelected world then
                let mousePositionWorld = World.mouseToWorld (entity.GetAbsolute world) data.Position world
                if  entity.GetVisible world &&
                    Math.isPointInBounds mousePositionWorld (entity.GetBounds world) then
                    if entity.GetEnabled world then
                        let world = entity.SetPressed true world
                        (Resolve, world)
                    else (Resolve, world)
                else (Cascade, world)
            else (Cascade, world)

        let handleMouseLeftUp evt world =
            let entity = evt.Subscriber : Entity
            let data = evt.Data : MouseButtonData
            if entity.GetSelected world then
                let wasPressed = entity.GetPressed world
                let world = entity.SetPressed false world
                let mousePositionWorld = World.mouseToWorld (entity.GetAbsolute world) data.Position world
                if  entity.GetVisible world &&
                    Math.isPointInBounds mousePositionWorld (entity.GetBounds world) then
                    if entity.GetEnabled world && wasPressed then
                        let world = entity.SetOpen (not (entity.GetOpen world)) world
                        let eventAddress = if entity.GetOpen world then Events.Open else Events.Close
                        let eventTrace = EventTrace.debug "ToggleDispatcher" "handleMouseLeftUp" EventTrace.empty
                        let world = World.publishPlus () (eventAddress --> entity) eventTrace entity true world
                        let eventTrace = EventTrace.record4 "ToggleDispatcher" "handleMouseLeftUp" "Toggle" EventTrace.empty
                        let world = World.publishPlus () (Events.Toggle --> entity) eventTrace entity true world
                        let world =
                            match entity.GetToggleSoundOpt world with
                            | Some toggleSound -> World.playSound (entity.GetToggleSoundVolume world) toggleSound world
                            | None -> world
                        (Resolve, world)
                    else (Resolve, world)
                else (Cascade, world)
            else (Cascade, world)

        static member Facets =
            [typeof<TextFacet>]

        static member Properties =
            [define Entity.Size (Vector2 (192.0f, 48.0f))
             define Entity.SwallowMouseLeft false
             define Entity.Open true
             define Entity.Pressed false
             define Entity.OpenImage Assets.Default.Image
             define Entity.ClosedImage Assets.Default.Image2
             define Entity.ToggleSoundOpt (Some Assets.Default.Sound)
             define Entity.ToggleSoundVolume Constants.Audio.SoundVolumeDefault]

        override this.Register (entity, world) =
            let world = bind (entity.Model<bool> ()) entity.Open world
            let world = bind entity.Open (entity.Model<bool> ()) world
            let world = World.monitor handleMouseLeftDown Events.MouseLeftDown entity world
            let world = World.monitor handleMouseLeftUp Events.MouseLeftUp entity world
            world

        override this.Actualize (entity, world) =
            if entity.GetVisible world then
                let transform = entity.GetTransform world
                let image =
                    if entity.GetOpen world && not (entity.GetPressed world)
                    then entity.GetOpenImage world
                    else entity.GetClosedImage world
                World.enqueueRenderMessage
                    (LayeredDescriptorMessage
                        { Elevation = transform.Elevation
                          PositionY = transform.Position.Y
                          AssetTag = AssetTag.generalize image
                          RenderDescriptor =
                            SpriteDescriptor
                                { Transform = transform
                                  Offset = Vector2.Zero
                                  InsetOpt = None
                                  Image = image
                                  Color = if entity.GetEnabled world then Color.White else entity.GetDisabledColor world
                                  Glow = Color.Zero
                                  Flip = FlipNone }})
                    world
            else world

        override this.GetQuickSize (entity, world) =
            match World.tryGetTextureSizeF (entity.GetOpenImage world) world with
            | Some size -> size
            | None -> Constants.Engine.EntitySizeDefault
            
[<AutoOpen>]
module FpsDispatcherModule =

    type Entity with
        member this.GetStartTickTime world : int64 = this.Get Property? StartTickTime world
        member this.SetStartTickTime (value : int64) world = this.SetFast Property? StartTickTime false value world
        member this.StartTickTime = lens Property? StartTickTime this.GetStartTickTime this.SetStartTickTime this
        member this.GetStartDateTime world : DateTime = this.Get Property? StartDateTime world
        member this.SetStartDateTime (value : DateTime) world = this.SetFast Property? StartDateTime false value world
        member this.StartDateTime = lens Property? StartDateTime this.GetStartDateTime this.SetStartDateTime this

    type FpsDispatcher () =
        inherit TextDispatcher ()

        static let resetIntermittent (entity : Entity) world =
            let startDateTime = entity.GetStartDateTime world
            let currentDateTime = DateTime.UtcNow
            let elapsedDateTime = currentDateTime - startDateTime
            if elapsedDateTime.TotalSeconds >= 4.0 then
                let world = entity.SetStartTickTime (World.getTickTime world) world
                entity.SetStartDateTime currentDateTime world
            else world

        static member Properties =
            [define Entity.StartTickTime 0L
             define Entity.StartDateTime DateTime.UtcNow]

        override this.Update (entity, world) =
            let world = resetIntermittent entity world
            let startDateTime = entity.GetStartDateTime world
            let currentDateTime = DateTime.UtcNow
            let elapsedDateTime = currentDateTime - startDateTime
            let time = double (World.getTickTime world - entity.GetStartTickTime world)
            let frames = time / elapsedDateTime.TotalSeconds
            if not (Double.IsNaN frames) then 
                let framesStr = "FPS: " + String.Format ("{0:f2}", frames)
                entity.SetText framesStr world
            else world

[<AutoOpen>]
module FeelerDispatcherModule =

    type Entity with
        member this.GetTouched world : bool = this.Get Property? Touched world
        member this.SetTouched (value : bool) world = this.SetFast Property? Touched false value world
        member this.Touched = lens Property? Touched this.GetTouched this.SetTouched this
        member this.TouchEvent = Events.Touch --> this
        member this.UntouchEvent = Events.Untouch --> this

    type FeelerDispatcher () =
        inherit GuiDispatcher ()

        let handleMouseLeftDown evt world =
            let entity = evt.Subscriber : Entity
            let data = evt.Data : MouseButtonData
            if entity.GetSelected world then
                let mousePositionWorld = World.mouseToWorld (entity.GetAbsolute world) data.Position world
                if  entity.GetVisible world &&
                    Math.isPointInBounds mousePositionWorld (entity.GetBounds world) then
                    if entity.GetEnabled world then
                        let world = entity.SetTouched true world
                        let eventTrace = EventTrace.debug "FeelerDispatcher" "handleMouseLeftDown" EventTrace.empty
                        let world = World.publishPlus data.Position (Events.Touch --> entity) eventTrace entity true world
                        (Resolve, world)
                    else (Resolve, world)
                else (Cascade, world)
            else (Cascade, world)

        let handleMouseLeftUp evt world =
            let entity = evt.Subscriber : Entity
            let data = evt.Data : MouseButtonData
            if entity.GetSelected world && entity.GetVisible world then
                if entity.GetEnabled world then
                    let world = entity.SetTouched false world
                    let eventTrace = EventTrace.debug "FeelerDispatcher" "handleMouseLeftDown" EventTrace.empty
                    let world = World.publishPlus data.Position (Events.Untouch --> entity) eventTrace entity true world
                    (Resolve, world)
                else (Resolve, world)
            else (Cascade, world)

        static member Properties =
            [define Entity.Size (Vector2 (192.0f, 48.0f))
             define Entity.SwallowMouseLeft false
             define Entity.Touched false]

        override this.Register (entity, world) =
            let world = World.monitor handleMouseLeftDown Events.MouseLeftDown entity world
            let world = World.monitor handleMouseLeftUp Events.MouseLeftUp entity world
            world

        override this.GetQuickSize (_, _) =
            Vector2 (192.0f, 48.0f)

[<AutoOpen>]
module FillBarDispatcherModule =

    type Entity with
        member this.GetFill world : single = this.Get Property? Fill world
        member this.SetFill (value : single) world = this.SetFast Property? Fill false value world
        member this.Fill = lens Property? Fill this.GetFill this.SetFill this
        member this.GetFillInset world : single = this.Get Property? FillInset world
        member this.SetFillInset (value : single) world = this.SetFast Property? FillInset false value world
        member this.FillInset = lens Property? FillInset this.GetFillInset this.SetFillInset this
        member this.GetFillImage world : Image AssetTag = this.Get Property? FillImage world
        member this.SetFillImage (value : Image AssetTag) world = this.SetFast Property? FillImage false value world
        member this.FillImage = lens Property? FillImage this.GetFillImage this.SetFillImage this
        member this.GetBorderImage world : Image AssetTag = this.Get Property? BorderImage world
        member this.SetBorderImage (value : Image AssetTag) world = this.SetFast Property? BorderImage false value world
        member this.BorderImage = lens Property? BorderImage this.GetBorderImage this.SetBorderImage this

    type FillBarDispatcher () =
        inherit GuiDispatcher ()
        
        let getFillBarSpriteDims (entity : Entity) world =
            let spriteSize = entity.GetSize world
            let spriteInset = spriteSize * entity.GetFillInset world * 0.5f
            let spritePosition = entity.GetPosition world + spriteInset
            let spriteWidth = (spriteSize.X - spriteInset.X * 2.0f) * entity.GetFill world
            let spriteHeight = spriteSize.Y - spriteInset.Y * 2.0f
            (spritePosition, Vector2 (spriteWidth, spriteHeight))

        static member Properties =
            [define Entity.Size (Vector2 (192.0f, 48.0f))
             define Entity.SwallowMouseLeft false
             define Entity.Fill 0.0f
             define Entity.FillInset 0.0f
             define Entity.FillImage Assets.Default.Image11
             define Entity.BorderImage Assets.Default.Image12]

        override this.Register (entity, world) =
            let world = bind (entity.Model<single> ()) entity.Fill world
            let world = bind entity.Fill (entity.Model<single> ()) world
            world

        override this.Actualize (entity, world) =
            if entity.GetVisible world then
                let borderSpriteTransform =
                    { Position = entity.GetPosition world
                      Size = entity.GetSize world
                      Rotation = 0.0f
                      Elevation = entity.GetElevation world + 0.5f
                      Flags = entity.GetFlags world }
                let (fillBarSpritePosition, fillBarSpriteSize) = getFillBarSpriteDims entity world
                let fillBarSpriteTransform =
                    { Position = fillBarSpritePosition
                      Size = fillBarSpriteSize
                      Rotation = 0.0f
                      Elevation = entity.GetElevation world
                      Flags = entity.GetFlags world }
                let fillBarColor = if entity.GetEnabled world then Color.White else entity.GetDisabledColor world
                World.enqueueRenderMessage
                    (LayeredDescriptorsMessage
                        [|{ Elevation = borderSpriteTransform.Elevation
                            PositionY = borderSpriteTransform.Position.Y
                            AssetTag = entity.GetBorderImage world |> AssetTag.generalize
                            RenderDescriptor =
                                SpriteDescriptor
                                    { Transform = borderSpriteTransform
                                      Offset = Vector2.Zero
                                      InsetOpt = None
                                      Image = entity.GetBorderImage world
                                      Color = fillBarColor
                                      Glow = Color.Zero
                                      Flip = FlipNone }}
                          { Elevation = fillBarSpriteTransform.Elevation
                            PositionY = fillBarSpriteTransform.Position.Y
                            AssetTag = entity.GetFillImage world |> AssetTag.generalize
                            RenderDescriptor =
                                SpriteDescriptor
                                    { Transform = fillBarSpriteTransform
                                      Offset = Vector2.Zero
                                      InsetOpt = None
                                      Image = entity.GetFillImage world
                                      Color = fillBarColor
                                      Glow = Color.Zero
                                      Flip = FlipNone }}|])
                    world
            else world

        override this.GetQuickSize (entity, world) =
            match World.tryGetTextureSizeF (entity.GetBorderImage world) world with
            | Some size -> size
            | None -> Constants.Engine.EntitySizeDefault

[<AutoOpen>]
module BlockDispatcherModule =

    type BlockDispatcher () =
        inherit EntityDispatcher ()

        static member Facets =
            [typeof<RigidBodyFacet>
             typeof<StaticSpriteFacet>]

        static member Properties =
            [define Entity.PublishChanges true
             define Entity.BodyType Static
             define Entity.StaticImage Assets.Default.Image4]

[<AutoOpen>]
module BoxDispatcherModule =

    type BoxDispatcher () =
        inherit EntityDispatcher ()

        static member Facets =
            [typeof<RigidBodyFacet>
             typeof<StaticSpriteFacet>]

        static member Properties =
            [define Entity.PublishChanges true
             define Entity.StaticImage Assets.Default.Image4]

[<AutoOpen>]
module CharacterDispatcherModule =

    type Entity with
        member this.GetCharacterIdleImage world = this.Get Property? CharacterIdleImage world
        member this.SetCharacterIdleImage value world = this.SetFast Property? CharacterIdleImage false value world
        member this.CharacterIdleImage = lens<Image AssetTag> Property? CharacterIdleImage this.GetCharacterIdleImage this.SetCharacterIdleImage this
        member this.GetCharacterJumpImage world = this.Get Property? CharacterJumpImage world
        member this.SetCharacterJumpImage value world = this.SetFast Property? CharacterJumpImage false value world
        member this.CharacterJumpImage = lens<Image AssetTag> Property? CharacterJumpImage this.GetCharacterJumpImage this.SetCharacterJumpImage this
        member this.GetCharacterWalkSheet world = this.Get Property? CharacterWalkSheet world
        member this.SetCharacterWalkSheet value world = this.SetFast Property? CharacterWalkSheet false value world
        member this.CharacterWalkSheet = lens<Image AssetTag> Property? CharacterWalkSheet this.GetCharacterWalkSheet this.SetCharacterWalkSheet this
        member this.GetCharacterFacingLeft world = this.Get Property? CharacterFacingLeft world
        member this.SetCharacterFacingLeft value world = this.SetFast Property? CharacterFacingLeft false value world
        member this.CharacterFacingLeft = lens<bool> Property? CharacterFacingLeft this.GetCharacterFacingLeft this.SetCharacterFacingLeft this

    type CharacterDispatcher () =
        inherit EntityDispatcher ()

        static let computeWalkCelInset (celSize : Vector2) (celRun : int) delay time =
            let timeCompressed = time / delay
            let frame = timeCompressed % int64 celRun
            let i = single (frame % 3L)
            let j = single (frame / 3L)
            let offset = v2 (i * celSize.X) (j * celSize.Y) 
            v4Bounds offset celSize

        static member Facets =
            [typeof<RigidBodyFacet>]

        static member Properties =
            [define Entity.PublishChanges true
             define Entity.CelSize (v2 28.0f 28.0f)
             define Entity.CelRun 8
             define Entity.AnimationDelay 4L
             define Entity.FixedRotation true
             define Entity.GravityScale 3.0f
             define Entity.BodyShape (BodyCapsule { Height = 0.5f; Radius = 0.25f; Center = v2Zero; PropertiesOpt = None })
             define Entity.CharacterIdleImage Assets.Default.CharacterIdleImage
             define Entity.CharacterJumpImage Assets.Default.CharacterJumpImage
             define Entity.CharacterWalkSheet Assets.Default.CharacterWalkImage
             define Entity.CharacterFacingLeft false]

        override this.Update (entity, world) =
            // we have to use a bit of hackery to remember whether the character is facing left or
            // right when there is no velocity
            let facingLeft = entity.GetCharacterFacingLeft world
            let velocity = World.getBodyLinearVelocity (entity.GetPhysicsId world) world
            if facingLeft && velocity.X > 1.0f then entity.SetCharacterFacingLeft false world
            elif not facingLeft && velocity.X < -1.0f then entity.SetCharacterFacingLeft true world
            else world

        override this.Actualize (entity, world) =
            if entity.GetVisible world && entity.GetInView world then
                let time = World.getTickTime world
                let physicsId = entity.GetPhysicsId world
                let facingLeft = entity.GetCharacterFacingLeft world
                let velocity = World.getBodyLinearVelocity physicsId world
                let celSize = entity.GetCelSize world
                let celRun = entity.GetCelRun world
                let animationDelay = entity.GetAnimationDelay world
                let transform = entity.GetTransform world
                let (insetOpt, image) =
                    if not (World.isBodyOnGround physicsId world) then
                        let image = entity.GetCharacterJumpImage world
                        (None, image)
                    elif velocity.X < 5.0f && velocity.X > -5.0f then
                        let image = entity.GetCharacterIdleImage world
                        (None, image)
                    else
                        let image = entity.GetCharacterWalkSheet world
                        (Some (computeWalkCelInset celSize celRun animationDelay time), image)
                World.enqueueRenderMessage
                    (LayeredDescriptorMessage
                        { Elevation = transform.Elevation
                          PositionY = transform.Position.Y
                          AssetTag = AssetTag.generalize image
                          RenderDescriptor =
                            SpriteDescriptor
                                { Transform = transform
                                  Offset = v2Zero
                                  InsetOpt = insetOpt
                                  Image = image
                                  Color = Color.White
                                  Glow = Color.Zero
                                  Flip = if facingLeft then FlipH else FlipNone }})
                    world
            else world

[<AutoOpen>]
module TileMapDispatcherModule =

    type TileMapDispatcher () =
        inherit EntityDispatcher ()

        static member Facets =
            [typeof<TileMapFacet>]

        static member Properties =
            [define Entity.Omnipresent true
             define Entity.PublishChanges true
             define Entity.Friction 0.0f
             define Entity.Restitution 0.0f
             define Entity.CollisionCategories "1"
             define Entity.CollisionMask "@"
             define Entity.TileMap Assets.Default.TileMap
             define Entity.Parallax 0.0f]

[<AutoOpen>]
module TmxMapDispatcherModule =

    type TmxMapDispatcher () =
        inherit EntityDispatcher ()

        static member Facets =
            [typeof<TmxMapFacet>]

        static member Properties =
            [define Entity.Omnipresent true
             define Entity.PublishChanges true
             define Entity.Friction 0.0f
             define Entity.Restitution 0.0f
             define Entity.CollisionCategories "1"
             define Entity.CollisionMask "@"
             define Entity.TmxMap (TmxMap Assets.Default.TmxFilePath)
             define Entity.Parallax 0.0f]

[<AutoOpen>]
module LayerDispatcherModule =

    type World with

        static member internal signalLayer<'model, 'message, 'command> signal (layer : Layer) world =
            match layer.GetDispatcher world with
            | :? LayerDispatcher<'model, 'message, 'command> as dispatcher ->
                Signal.processSignal dispatcher.Message dispatcher.Command (layer.Model<'model> ()) signal layer world
            | _ ->
                Log.info "Failed to send signal to layer."
                world

    and Layer with

        member this.UpdateModel<'model> updater world =
            this.SetModel<'model> (updater (this.GetModel<'model> world)) world

        member this.Signal<'model, 'message, 'command> signal world =
            World.signalLayer<'model, 'message, 'command> signal this world

    and [<AbstractClass>] LayerDispatcher<'model, 'message, 'command> (initial : 'model) =
        inherit LayerDispatcher ()

        member this.GetModel (layer : Layer) world : 'model =
            layer.GetModel<'model> world

        member this.SetModel (model : 'model) (layer : Layer) world =
            layer.SetModel<'model> model world

        member this.Model (layer : Layer) =
            lens Property? Model (this.GetModel layer) (flip this.SetModel layer) layer

        override this.Register (layer, world) =
            let world =
                let property = World.getLayerModelProperty layer world
                if property.DesignerType = typeof<unit>
                then layer.SetModel<'model> initial world
                else world
            let channels = this.Channel (this.Model layer, layer)
            let world = Signal.processChannels this.Message this.Command (this.Model layer) channels layer world
            let content = this.Content (this.Model layer, layer)
            let world =
                List.fold (fun world content ->
                    World.expandEntityContent content (SimulantOrigin layer) layer layer world |> snd)
                    world content
            let initializers = this.Initializers (this.Model layer, layer)
            List.fold (fun world initializer ->
                match initializer with
                | PropertyDefinition def ->
                    let propertyName = def.PropertyName
                    let alwaysPublish = Reflection.isPropertyAlwaysPublishByName propertyName
                    let property = { PropertyType = def.PropertyType; PropertyValue = PropertyExpr.eval def.PropertyExpr world }
                    World.setProperty def.PropertyName alwaysPublish property layer world
                | EventHandlerDefinition (handler, partialAddress) ->
                    let eventAddress = partialAddress --> layer
                    World.monitor (fun (evt : Event) world ->
                        let world = WorldModule.trySignal (handler evt) layer world
                        (Cascade, world))
                        eventAddress (layer :> Simulant) world
                | BindDefinition (left, right) ->
                    WorldModule.bind5 layer left right world)
                world initializers

        override this.Actualize (layer, world) =
            let view = this.View (this.GetModel layer world, layer, world)
            World.actualizeView view world

        override this.TrySignal (signalObj, layer, world) =
            match signalObj with
            | :? Signal<'message, obj> as signal -> layer.Signal<'model, 'message, 'command> (match signal with Message message -> msg message | _ -> failwithumf ()) world
            | :? Signal<obj, 'command> as signal -> layer.Signal<'model, 'message, 'command> (match signal with Command command -> cmd command | _ -> failwithumf ()) world
            | _ -> Log.info "Incorrect signal type returned from event binding."; world

        abstract member Initializers : Lens<'model, World> * Layer -> PropertyInitializer list
        default this.Initializers (_, _) = []

        abstract member Channel : Lens<'model, World> * Layer -> Channel<'message, 'command, Layer, World> list
        default this.Channel (_, _) = []

        abstract member Message : 'model * 'message * Layer * World -> Signal<'message, 'command> list * 'model
        default this.Message (model, _, _, _) = just model

        abstract member Command : 'model * 'command * Layer * World -> Signal<'message, 'command> list * World
        default this.Command (_, _, _, world) = just world

        abstract member Content : Lens<'model, World> * Layer -> EntityContent list
        default this.Content (_, _) = []

        abstract member View : 'model * Layer * World -> View
        default this.View (_, _, _) = View.empty

[<AutoOpen>]
module ScreenDispatcherModule =

    type World with

        static member internal signalScreen<'model, 'message, 'command> signal (screen : Screen) world =
            match screen.GetDispatcher world with
            | :? ScreenDispatcher<'model, 'message, 'command> as dispatcher ->
                Signal.processSignal dispatcher.Message dispatcher.Command (screen.Model<'model> ()) signal screen world
            | _ ->
                Log.info "Failed to send signal to screen."
                world

    and Screen with

        member this.UpdateModel<'model> updater world =
            this.SetModel<'model> (updater (this.GetModel<'model> world)) world

        member this.Signal<'model, 'message, 'command> signal world =
            World.signalScreen<'model, 'message, 'command> signal this world

    and [<AbstractClass>] ScreenDispatcher<'model, 'message, 'command> (initial : 'model) =
        inherit ScreenDispatcher ()

        member this.GetModel (screen : Screen) world : 'model =
            screen.GetModel<'model> world

        member this.SetModel (model : 'model) (screen : Screen) world =
            screen.SetModel<'model> model world

        member this.Model (screen : Screen) =
            lens Property? Model (this.GetModel screen) (flip this.SetModel screen) screen

        override this.Register (screen, world) =
            let world =
                let property = World.getScreenModelProperty screen world
                if property.DesignerType = typeof<unit>
                then screen.SetModel<'model> initial world
                else world
            let channels = this.Channel (this.Model screen, screen)
            let world = Signal.processChannels this.Message this.Command (this.Model screen) channels screen world
            let content = this.Content (this.Model screen, screen)
            let world =
                List.fold (fun world content ->
                    World.expandLayerContent content (SimulantOrigin screen) screen world |> snd)
                    world content
            let initializers = this.Initializers (this.Model screen, screen)
            List.fold (fun world initializer ->
                match initializer with
                | PropertyDefinition def ->
                    let propertyName = def.PropertyName
                    let alwaysPublish = Reflection.isPropertyAlwaysPublishByName propertyName
                    let property = { PropertyType = def.PropertyType; PropertyValue = PropertyExpr.eval def.PropertyExpr world }
                    World.setProperty def.PropertyName alwaysPublish property screen world
                | EventHandlerDefinition (handler, partialAddress) ->
                    let eventAddress = partialAddress --> screen
                    World.monitor (fun (evt : Event) world ->
                        let world = WorldModule.trySignal (handler evt) screen world
                        (Cascade, world))
                        eventAddress (screen :> Simulant) world
                | BindDefinition (left, right) ->
                    WorldModule.bind5 screen left right world)
                world initializers

        override this.Actualize (screen, world) =
            let view = this.View (this.GetModel screen world, screen, world)
            World.actualizeView view world

        override this.TrySignal (signalObj, screen, world) =
            match signalObj with
            | :? Signal<'message, obj> as signal -> screen.Signal<'model, 'message, 'command> (match signal with Message message -> msg message | _ -> failwithumf ()) world
            | :? Signal<obj, 'command> as signal -> screen.Signal<'model, 'message, 'command> (match signal with Command command -> cmd command | _ -> failwithumf ()) world
            | _ -> Log.info "Incorrect signal type returned from event binding."; world

        abstract member Initializers : Lens<'model, World> * Screen -> PropertyInitializer list
        default this.Initializers (_, _) = []

        abstract member Channel : Lens<'model, World> * Screen -> Channel<'message, 'command, Screen, World> list
        default this.Channel (_, _) = []

        abstract member Message : 'model * 'message * Screen * World -> Signal<'message, 'command> list * 'model
        default this.Message (model, _, _, _) = just model

        abstract member Command : 'model * 'command * Screen * World -> Signal<'message, 'command> list * World
        default this.Command (_, _, _, world) = just world

        abstract member Content : Lens<'model, World> * Screen -> LayerContent list
        default this.Content (_, _) = []

        abstract member View : 'model * Screen * World -> View
        default this.View (_, _, _) = View.empty