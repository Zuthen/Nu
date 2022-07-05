﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2020.

namespace OpenGL
open System
open System.IO
open System.Numerics
open System.Runtime.InteropServices
open Prime
open Nu

[<RequireQualifiedAccess>]
module PhysicallyBased =

    /// Describes a physically-based material.
    type [<StructuralEquality; NoComparison; Struct>] PhysicallyBasedMaterial =
        { AlbedoTexture : uint
          MetalnessTexture : uint
          RoughnessTexture : uint
          NormalTexture : uint
          AmbientOcclusionTexture : uint }

    /// Describes some physically-based geometry that's loaded into VRAM.
    type [<StructuralEquality; NoComparison>] PhysicallyBasedGeometry =
        { Bounds : Box3
          PrimitiveType : PrimitiveType
          ElementCount : int
          Vertices : Vector3 array
          VertexBuffer : uint
          ModelBuffer : uint
          IndexBuffer : uint
          PhysicallyBasedVao : uint }

    /// Describes a renderable physically-based surface.
    type [<CustomEquality; NoComparison; Struct>] PhysicallyBasedSurface =
        { mutable HashCode : int
          SurfaceMatrixIsIdentity : bool
          SurfaceMatrix : Matrix4x4
          SurfaceBounds : Box3
          PhysicallyBasedMaterial : PhysicallyBasedMaterial // OPTIMIZATION: avoid matrix multiply when unecessary.
          PhysicallyBasedGeometry : PhysicallyBasedGeometry }

        static member inline hash surface =
            hash surface.SurfaceMatrix ^^^
            hash surface.PhysicallyBasedMaterial ^^^
            hash surface.PhysicallyBasedGeometry

        static member inline make (surfaceMatrix : Matrix4x4) surfaceBounds physicallyBasedMaterial physicallyBasedGeometry =
            let mutable result =
                { HashCode = 0
                  SurfaceMatrixIsIdentity = surfaceMatrix.IsIdentity
                  SurfaceMatrix = surfaceMatrix
                  SurfaceBounds = surfaceBounds
                  PhysicallyBasedMaterial = physicallyBasedMaterial
                  PhysicallyBasedGeometry = physicallyBasedGeometry }
            result.HashCode <- PhysicallyBasedSurface.hash result
            result

        static member inline equals left right =
            left.HashCode = right.HashCode &&
            left.SurfaceMatrix = right.SurfaceMatrix &&
            left.PhysicallyBasedMaterial = right.PhysicallyBasedMaterial &&
            left.PhysicallyBasedGeometry = right.PhysicallyBasedGeometry

        member this.Equals that =
            PhysicallyBasedSurface.equals this that

        override this.Equals (thatObj : obj) =
            match thatObj with
            | :? PhysicallyBasedSurface as that -> PhysicallyBasedSurface.equals this that
            | _ -> false

        override this.GetHashCode () =
            PhysicallyBasedSurface.hash this

    /// A physically-based static model.
    type [<ReferenceEquality; NoComparison>] PhysicallyBasedStaticModel =
        { Bounds : Box3
          Surfaces : PhysicallyBasedSurface array }

    /// Describes a physically-based shader that's loaded into GPU.
    type [<StructuralEquality; NoComparison>] PhysicallyBasedShader =
        { ViewUniform : int
          ProjectionUniform : int
          EyePositionUniform : int
          AlbedoTextureUniform : int
          MetalnessTextureUniform : int
          RoughnessTextureUniform : int
          NormalTextureUniform : int
          AmbientOcclusionTextureUniform : int
          IrradianceMapUniform : int
          EnvironmentFilterMapUniform : int
          BrdfTextureUniform : int
          LightPositionsUniform : int
          LightColorsUniform : int
          LightBrightnessesUniform : int
          LightIntensitiesUniform : int
          PhysicallyBasedShader : uint }

    /// Describes a second pass of a deferred physically-based shader that's loaded into GPU.
    type [<StructuralEquality; NoComparison>] PhysicallyBasedDeferred2Shader =
        { EyePositionUniform : int
          PositionTextureUniform : int
          NormalTextureUniform : int
          AlbedoTextureUniform : int
          MaterialTextureUniform : int
          IrradianceMapUniform : int
          EnvironmentFilterMapUniform : int
          BrdfTextureUniform : int
          LightPositionsUniform : int
          LightBrightnessesUniform : int
          LightIntensitiesUniform : int
          LightColorsUniform : int
          PhysicallyBasedDeferred2Shader : uint }

    /// Attempt to create physically-based from an assimp mesh.
    let TryCreatePhysicallyBasedMesh (mesh : Assimp.Mesh) =

        // ensure required data is available
        if  mesh.HasVertices &&
            mesh.HasNormals &&
            mesh.HasTextureCoords 0 then

            // attempt to populate geometry data
            if mesh.Vertices.Count = mesh.Normals.Count && mesh.Vertices.Count = mesh.TextureCoordinateChannels.[0].Count then

                // populate vertex data and bounds
                let vertexData = Array.zeroCreate<single> (mesh.Vertices.Count * 8)
                let mutable positionMin = v3Zero
                let mutable positionMax = v3Zero
                for i in 0 .. dec mesh.Vertices.Count do
                    let v = i * 8
                    let position = mesh.Vertices.[i]
                    let normal = mesh.Normals.[i]
                    let texCoords = mesh.TextureCoordinateChannels.[0].[i]
                    vertexData.[v] <- position.X
                    vertexData.[v+1] <- position.Y
                    vertexData.[v+2] <- position.Z
                    vertexData.[v+3] <- normal.X
                    vertexData.[v+4] <- normal.Y
                    vertexData.[v+5] <- normal.Z
                    vertexData.[v+6] <- texCoords.X
                    vertexData.[v+7] <- 1.0f - texCoords.Y
                    positionMin.X <- min positionMin.X position.X
                    positionMin.Y <- min positionMin.Y position.Y
                    positionMin.Z <- min positionMin.Z position.Z
                    positionMax.X <- max positionMax.X position.X
                    positionMax.Y <- max positionMax.Y position.Y
                    positionMax.Z <- max positionMax.Z position.Z
                let bounds = box3 positionMin (positionMax - positionMin)

                // populate triangle index data
                let indexList = SegmentedList.make ()
                for face in mesh.Faces do
                    let indices = face.Indices
                    if indices.Count = 3 then
                        // NOTE: .obj files exported from blender seem to have reverse winding order, so we add indices in reverse
                        SegmentedList.add indices.[2] indexList
                        SegmentedList.add indices.[1] indexList
                        SegmentedList.add indices.[0] indexList
                let indexData = Seq.toArray indexList

                // fin
                Right (vertexData, indexData, bounds)
                    
            // error
            else Left ("Vertex / normal / tex coords count mismatch.")

        // error
        else Left "Mesh is missing vertices, normals, or texCoords."

    /// Create a mesh for a physically-based quad.
    let CreatePhysicallyBasedQuadMesh () =

        // make vertex data
        let vertexData =
            [|
                (*   positions   *)       (*    normals    *)         (* tex coords *)
                -1.0f; -1.0f; +0.0f;       0.0f;  0.0f; +1.0f;        0.0f; 0.0f; // bottom-left
                +1.0f; -1.0f; +0.0f;       0.0f;  0.0f; +1.0f;        1.0f; 0.0f; // bottom-right
                +1.0f; +1.0f; +0.0f;       0.0f;  0.0f; +1.0f;        1.0f; 1.0f; // top-right
                +1.0f; +1.0f; +0.0f;       0.0f;  0.0f; +1.0f;        1.0f; 1.0f; // top-right
                -1.0f; +1.0f; +0.0f;       0.0f;  0.0f; +1.0f;        0.0f; 1.0f; // top-left
                -1.0f; -1.0f; +0.0f;       0.0f;  0.0f; +1.0f;        0.0f; 0.0f; // bottom-left
            |]

        // make index data trivially
        let indexData = Array.init 6 id

        // make bounds trivially
        let bounds = box3 (v3 -1.0f -1.0f 0.0f) (v3 2.0f 2.0f 0.0f)

        // fin
        (vertexData, indexData, bounds)

    /// Create a mesh for a physically-based cube.
    let CreatePhysicallyBasedCubeMesh () =

        // make vertex data
        let vertexData =
            [|
                (*   positions   *)       (*    normals    *)         (* tex coords *)

                // back face
                -0.5f; -0.5f; -0.5f;       0.0f;  0.0f; -1.0f;        0.0f; 0.0f; // bottom-left
                +0.5f; +0.5f; -0.5f;       0.0f;  0.0f; -1.0f;        1.0f; 1.0f; // top-right
                +0.5f; -0.5f; -0.5f;       0.0f;  0.0f; -1.0f;        1.0f; 0.0f; // bottom-right         
                +0.5f; +0.5f; -0.5f;       0.0f;  0.0f; -1.0f;        1.0f; 1.0f; // top-right
                -0.5f; -0.5f; -0.5f;       0.0f;  0.0f; -1.0f;        0.0f; 0.0f; // bottom-left
                -0.5f; +0.5f; -0.5f;       0.0f;  0.0f; -1.0f;        0.0f; 1.0f; // top-left

                // front face
                -0.5f; -0.5f; +0.5f;       0.0f;  0.0f; +1.0f;        0.0f; 0.0f; // bottom-left
                +0.5f; -0.5f; +0.5f;       0.0f;  0.0f; +1.0f;        1.0f; 0.0f; // bottom-right
                +0.5f; +0.5f; +0.5f;       0.0f;  0.0f; +1.0f;        1.0f; 1.0f; // top-right
                +0.5f; +0.5f; +0.5f;       0.0f;  0.0f; +1.0f;        1.0f; 1.0f; // top-right
                -0.5f; +0.5f; +0.5f;       0.0f;  0.0f; +1.0f;        0.0f; 1.0f; // top-left
                -0.5f; -0.5f; +0.5f;       0.0f;  0.0f; +1.0f;        0.0f; 0.0f; // bottom-left

                // left face
                -0.5f; +0.5f; +0.5f;      -1.0f;  0.0f;  0.0f;        1.0f; 0.0f; // top-right
                -0.5f; +0.5f; -0.5f;      -1.0f;  0.0f;  0.0f;        1.0f; 1.0f; // top-left
                -0.5f; -0.5f; -0.5f;      -1.0f;  0.0f;  0.0f;        0.0f; 1.0f; // bottom-left
                -0.5f; -0.5f; -0.5f;      -1.0f;  0.0f;  0.0f;        0.0f; 1.0f; // bottom-left
                -0.5f; -0.5f; +0.5f;      -1.0f;  0.0f;  0.0f;        0.0f; 0.0f; // bottom-right
                -0.5f; +0.5f; +0.5f;      -1.0f;  0.0f;  0.0f;        1.0f; 0.0f; // top-right

                // right face
                +0.5f; +0.5f; +0.5f;      +1.0f;  0.0f;  0.0f;        1.0f; 0.0f; // top-left
                +0.5f; -0.5f; -0.5f;      +1.0f;  0.0f;  0.0f;        0.0f; 1.0f; // bottom-right
                +0.5f; +0.5f; -0.5f;      +1.0f;  0.0f;  0.0f;        1.0f; 1.0f; // top-right         
                +0.5f; -0.5f; -0.5f;      +1.0f;  0.0f;  0.0f;        0.0f; 1.0f; // bottom-right
                +0.5f; +0.5f; +0.5f;      +1.0f;  0.0f;  0.0f;        1.0f; 0.0f; // top-left
                +0.5f; -0.5f; +0.5f;      +1.0f;  0.0f;  0.0f;        0.0f; 0.0f; // bottom-left     

                // bottom face
                -0.5f; -0.5f; -0.5f;       0.0f; -1.0f;  0.0f;        0.0f; 1.0f; // top-right
                +0.5f; -0.5f; -0.5f;       0.0f; -1.0f;  0.0f;        1.0f; 1.0f; // top-left
                +0.5f; -0.5f; +0.5f;       0.0f; -1.0f;  0.0f;        1.0f; 0.0f; // bottom-left
                +0.5f; -0.5f; +0.5f;       0.0f; -1.0f;  0.0f;        1.0f; 0.0f; // bottom-left
                -0.5f; -0.5f; +0.5f;       0.0f; -1.0f;  0.0f;        0.0f; 0.0f; // bottom-right
                -0.5f; -0.5f; -0.5f;       0.0f; -1.0f;  0.0f;        0.0f; 1.0f; // top-right

                // top face
                -0.5f; +0.5f; -0.5f;       0.0f; +1.0f;  0.0f;        0.0f; 1.0f; // top-left
                +0.5f; +0.5f ;+0.5f;       0.0f; +1.0f;  0.0f;        1.0f; 0.0f; // bottom-right
                +0.5f; +0.5f; -0.5f;       0.0f; +1.0f;  0.0f;        1.0f; 1.0f; // top-right     
                +0.5f; +0.5f; +0.5f;       0.0f; +1.0f;  0.0f;        1.0f; 0.0f; // bottom-right
                -0.5f; +0.5f; -0.5f;       0.0f; +1.0f;  0.0f;        0.0f; 1.0f; // top-left
                -0.5f; +0.5f; +0.5f;       0.0f; +1.0f;  0.0f;        0.0f; 0.0f  // bottom-left     
            |]

        // make index data trivially
        let indexData = Array.init 36 id

        // make bounds trivially
        let bounds = box3 (v3Dup -0.5f) v3One

        // fin
        (vertexData, indexData, bounds)

    /// Create physically-based geometry from a mesh.
    let CreatePhysicallyBasedGeometry (renderable, vertexData : single array, indexData : int array, bounds) =

        // make buffers
        let (vertices, vertexBuffer, modelBuffer, indexBuffer, vao) =

            // make renderable
            if renderable then

                // initialize vao
                let vao = Gl.GenVertexArray ()
                Gl.BindVertexArray vao
                Hl.Assert ()

                // create vertex buffer
                let vertexBuffer = Gl.GenBuffer ()
                let normalOffset =      (3 (*position*)) * sizeof<single>
                let texCoordsOffset =   (3 (*position*) + 3 (*normal*)) * sizeof<single>
                let vertexSize =        (3 (*position*) + 3 (*normal*) + 2 (*texCoords*)) * sizeof<single>
                Gl.BindBuffer (BufferTarget.ArrayBuffer, vertexBuffer)
                let vertexDataPtr = GCHandle.Alloc (vertexData, GCHandleType.Pinned)
                try Gl.BufferData (BufferTarget.ArrayBuffer, uint (vertexData.Length * sizeof<single>), vertexDataPtr.AddrOfPinnedObject (), BufferUsage.StaticDraw)
                finally vertexDataPtr.Free ()
                Gl.EnableVertexAttribArray 0u
                Gl.VertexAttribPointer (0u, 3, VertexAttribType.Float, false, vertexSize, nativeint 0)
                Gl.EnableVertexAttribArray 1u
                Gl.VertexAttribPointer (1u, 3, VertexAttribType.Float, false, vertexSize, nativeint normalOffset)
                Gl.EnableVertexAttribArray 2u
                Gl.VertexAttribPointer (2u, 2, VertexAttribType.Float, false, vertexSize, nativeint texCoordsOffset)
                Hl.Assert ()

                // create model buffer
                let modelBuffer = Gl.GenBuffer ()
                Gl.BindBuffer (BufferTarget.ArrayBuffer, modelBuffer)
                let modelDataPtr = GCHandle.Alloc (m4Identity.ToArray (), GCHandleType.Pinned)
                try Gl.BufferData (BufferTarget.ArrayBuffer, uint (16 * sizeof<single>), modelDataPtr.AddrOfPinnedObject (), BufferUsage.StreamDraw)
                finally modelDataPtr.Free ()
                Gl.EnableVertexAttribArray 3u
                Gl.VertexAttribPointer (3u, 4, VertexAttribType.Float, false, 16 * sizeof<single>, nativeint 0)
                Gl.VertexAttribDivisor (3u, 1u)
                Gl.EnableVertexAttribArray 4u
                Gl.VertexAttribPointer (4u, 4, VertexAttribType.Float, false, 16 * sizeof<single>, nativeint (4 * sizeof<single>))
                Gl.VertexAttribDivisor (4u, 1u)
                Gl.EnableVertexAttribArray 5u
                Gl.VertexAttribPointer (5u, 4, VertexAttribType.Float, false, 16 * sizeof<single>, nativeint (8 * sizeof<single>))
                Gl.VertexAttribDivisor (5u, 1u)
                Gl.EnableVertexAttribArray 6u
                Gl.VertexAttribPointer (6u, 4, VertexAttribType.Float, false, 16 * sizeof<single>, nativeint (12 * sizeof<single>))
                Gl.VertexAttribDivisor (6u, 1u)
                Hl.Assert ()

                // create index buffer
                let indexBuffer = Gl.GenBuffer ()
                Gl.BindBuffer (BufferTarget.ElementArrayBuffer, indexBuffer)
                let indexDataSize = uint (indexData.Length * sizeof<uint>)
                let indexDataPtr = GCHandle.Alloc (indexData, GCHandleType.Pinned)
                try Gl.BufferData (BufferTarget.ElementArrayBuffer, indexDataSize, indexDataPtr.AddrOfPinnedObject (), BufferUsage.StaticDraw)
                finally indexDataPtr.Free ()
                Hl.Assert ()

                // finalize vao
                Gl.BindVertexArray 0u
                Hl.Assert ()

                // fin
                ([||], vertexBuffer, modelBuffer, indexBuffer, vao)

            // fake buffers
            else
                
                // compute vertices
                let vertices = Array.zeroCreate (vertexData.Length / 3)
                for i in 0 .. dec vertices.Length do
                    let j = i * 3
                    let vertex = v3 vertexData.[j] vertexData.[j+1] vertexData.[j+2]
                    vertices.[i] <- vertex
                
                // fin
                (vertices, 0u, 0u, 0u, 0u)

        // make physically-based geometry
        let geometry =
            { Bounds = bounds
              PrimitiveType = PrimitiveType.Triangles
              ElementCount = indexData.Length
              Vertices = vertices
              VertexBuffer = vertexBuffer
              ModelBuffer = modelBuffer
              IndexBuffer = indexBuffer
              PhysicallyBasedVao = vao }

        // fin
        geometry

    /// Attempt to create physically-based geometry from an assimp mesh.
    let TryCreatePhysicallyBasedGeometry (renderable, mesh : Assimp.Mesh) =
        let meshOpt =
#if DEBUG_RENDERING_CUBE
            ignore<Assimp.Mesh> mesh
            Right (CreatePhysicallyBasedCubeMesh ())
#else
            TryCreatePhysicallyBasedMesh mesh
#endif
        match meshOpt with
        | Right (vertexData, indexData, bounds) -> Right (CreatePhysicallyBasedGeometry (renderable, vertexData, indexData, bounds))
        | Left error -> Left error

    /// Create physically-based quad.
    let CreatePhysicallyBasedQuad renderable =
        let (vertexData, indexData, bounds) = CreatePhysicallyBasedQuadMesh ()
        CreatePhysicallyBasedGeometry (renderable, vertexData, indexData, bounds)

    /// Create physically-based cube.
    let CreatePhysicallyBasedCube renderable =
        let (vertexData, indexData, bounds) = CreatePhysicallyBasedCubeMesh ()
        CreatePhysicallyBasedGeometry (renderable, vertexData, indexData, bounds)

    /// Attempt to create physically-based material from an assimp mesh.
    let TryCreatePhysicallyBasedMaterial (dirPath : string, material : Assimp.Material) =
        if  material.HasTextureDiffuse && // mapped to albedo
            material.HasTextureSpecular && // mapped to metalness
            material.HasTextureHeight && // mapped to roughness
            material.HasTextureNormal && // mapped to normal
            material.HasTextureAmbient then // mapped to ambient occlusion
            let (_, albedo) = material.GetMaterialTexture (Assimp.TextureType.Diffuse, 0)
            let (_, metalness) = material.GetMaterialTexture (Assimp.TextureType.Specular, 0)
            let (_, roughness) = material.GetMaterialTexture (Assimp.TextureType.Height, 0)
            let (_, normal) = material.GetMaterialTexture (Assimp.TextureType.Normals, 0)
            let (_, ambientOcclusion) = material.GetMaterialTexture (Assimp.TextureType.Ambient, 0)
            let albedoFilePath = Path.Combine (dirPath, albedo.FilePath)
            let metalnessFilePath = Path.Combine (dirPath, metalness.FilePath)
            let roughnessFilePath = Path.Combine (dirPath, roughness.FilePath)
            let normalFilePath = Path.Combine (dirPath, normal.FilePath)
            let ambientOcclusionFilePath = Path.Combine (dirPath, ambientOcclusion.FilePath)
            match Texture.TryCreateTexture2d (TextureMinFilter.Linear, TextureMagFilter.Linear, albedoFilePath) with
            | Right albedoTexture ->
                match Texture.TryCreateTexture2d (TextureMinFilter.Linear, TextureMagFilter.Linear, metalnessFilePath) with
                | Right metalnessTexture ->
                    match Texture.TryCreateTexture2d (TextureMinFilter.Linear, TextureMagFilter.Linear, roughnessFilePath) with
                    | Right roughnessTexture ->
                        match Texture.TryCreateTexture2d (TextureMinFilter.Linear, TextureMagFilter.Linear, normalFilePath) with
                        | Right normalTexture ->
                            match Texture.TryCreateTexture2d (TextureMinFilter.Linear, TextureMagFilter.Linear, ambientOcclusionFilePath) with
                            | Right ambientOcclusionTexture -> Right (albedoTexture, metalnessTexture, roughnessTexture, normalTexture, ambientOcclusionTexture)
                            | Left error -> Left ("Could not load texture '" + ambientOcclusionFilePath + "' due to '" + error + "'.")
                        | Left error -> Left ("Could not load texture '" + normalFilePath + "' due to '" + error + "'.")
                    | Left error -> Left ("Could not load texture '" + roughnessFilePath + "' due to '" + error + "'.")
                | Left error -> Left ("Could not load texture '" + metalnessFilePath + "' due to '" + error + "'.")
            | Left error -> Left ("Could not load texture '" + albedoFilePath + "' due to '" + error + "'.")
        else Left ("Could not create physically-based material due to missing diffuse/albedo, metalness, roughness, normal, or ambientOcclusion texture.")

    /// Attempt to create physically-based material from an assimp scene.
    let TryCreatePhysicallyBasedMaterials (renderable, dirPath : string, scene : Assimp.Scene) =
        if renderable then
            let mutable errorOpt = None
            let materials = Array.zeroCreate scene.Materials.Count
            for i in 0 .. dec scene.Materials.Count do
                if Option.isNone errorOpt then
                    match TryCreatePhysicallyBasedMaterial (dirPath, scene.Materials.[i]) with
                    | Right ((_, albedoTexture), (_, metalnessTexture), (_, roughnessTexture), (_, normalTexture), (_, ambientOcclusion)) ->
                        materials.[i] <-
                            { AlbedoTexture = albedoTexture
                              MetalnessTexture = metalnessTexture
                              RoughnessTexture = roughnessTexture
                              NormalTexture = normalTexture
                              AmbientOcclusionTexture = ambientOcclusion }
                    | Left error -> errorOpt <- Some error
            match errorOpt with
            | Some error -> Left error
            | None -> Right materials
        else Right [||]

    let TryCreatePhysicallyBasedGeometries (renderable, filePath, scene : Assimp.Scene) =
        let mutable errorOpt = None
        let geometries = SegmentedList.make ()
        for mesh in scene.Meshes do
            if Option.isNone errorOpt then
                match TryCreatePhysicallyBasedGeometry (renderable, mesh) with
                | Right geometry -> SegmentedList.add geometry geometries
                | Left error -> errorOpt <- Some ("Could not load geometry for mesh in file name '" + filePath + "' due to: " + error)
        match errorOpt with
        | Some error -> Left error
        | None -> Right geometries

    let TryCreatePhysicallyBasedStaticModel (renderable, filePath, assimp : Assimp.AssimpContext) =
        try let scene = assimp.ImportFile (filePath, Assimp.PostProcessSteps.CalculateTangentSpace ||| Assimp.PostProcessSteps.JoinIdenticalVertices ||| Assimp.PostProcessSteps.Triangulate ||| Assimp.PostProcessSteps.GenerateSmoothNormals ||| Assimp.PostProcessSteps.SplitLargeMeshes ||| Assimp.PostProcessSteps.LimitBoneWeights ||| Assimp.PostProcessSteps.RemoveRedundantMaterials ||| Assimp.PostProcessSteps.SortByPrimitiveType ||| Assimp.PostProcessSteps.FindDegenerates ||| Assimp.PostProcessSteps.FindInvalidData ||| Assimp.PostProcessSteps.GenerateUVCoords ||| Assimp.PostProcessSteps.FlipWindingOrder)
            let dirPath = Path.GetDirectoryName filePath
            match TryCreatePhysicallyBasedMaterials (renderable, dirPath, scene) with
            | Right materials ->
                match TryCreatePhysicallyBasedGeometries (renderable, filePath, scene) with
                | Right geometries ->
                    let surfaces = SegmentedList.make ()
                    let mutable bounds = box3Zero
                    for (node, nodeTransform) in scene.RootNode.CollectNodesAndTransforms m4Identity do
                        for meshIndex in node.MeshIndices do
                            let materialIndex = scene.Meshes.[meshIndex].MaterialIndex
                            let material = if renderable then materials.[materialIndex] else Unchecked.defaultof<_>
                            let geometry = geometries.[meshIndex]
                            let surface = PhysicallyBasedSurface.make nodeTransform geometry.Bounds material geometry
                            SegmentedList.add surface surfaces
                            bounds <- bounds.Combine (geometry.Bounds.Transform nodeTransform)
                    Right { Bounds = bounds; Surfaces = Array.ofSeq surfaces }
                | Left error -> Left error
            | Left error -> Left ("Could not load materials for static model in file name '" + filePath + "' due to: " + error)
        with exn -> Left ("Could not load static model '" + filePath + "' due to: " + scstring exn)

    let CreatePhysicallyBasedShader (shaderFilePath : string) =

        // create shader
        let shader = Shader.CreateShaderFromFilePath shaderFilePath

        // retrieve uniforms
        let viewUniform = Gl.GetUniformLocation (shader, "view")
        let projectionUniform = Gl.GetUniformLocation (shader, "projection")
        let eyePositionUniform = Gl.GetUniformLocation (shader, "eyePosition")
        let albedoTextureUniform = Gl.GetUniformLocation (shader, "albedoTexture")
        let metalnessTextureUniform = Gl.GetUniformLocation (shader, "metalnessTexture")
        let roughnessTextureUniform = Gl.GetUniformLocation (shader, "roughnessTexture")
        let normalTextureUniform = Gl.GetUniformLocation (shader, "normalTexture")
        let ambientOcclusionTextureUniform = Gl.GetUniformLocation (shader, "ambientOcclusionTexture")
        let irradianceMapUniform = Gl.GetUniformLocation (shader, "irradianceMap")
        let environmentFilterMapUniform = Gl.GetUniformLocation (shader, "environmentFilterMap")
        let brdfTextureUniform = Gl.GetUniformLocation (shader, "brdfTexture")
        let lightPositionsUniform = Gl.GetUniformLocation (shader, "lightPositions")
        let lightBrightnessesUniform = Gl.GetUniformLocation (shader, "lightBrightnesses")
        let lightIntensitiesUniform = Gl.GetUniformLocation (shader, "lightIntensities")
        let lightColorsUniform = Gl.GetUniformLocation (shader, "lightColors")

        // make shader record
        { ViewUniform = viewUniform
          ProjectionUniform = projectionUniform
          EyePositionUniform = eyePositionUniform
          AlbedoTextureUniform = albedoTextureUniform
          MetalnessTextureUniform = metalnessTextureUniform
          RoughnessTextureUniform = roughnessTextureUniform
          NormalTextureUniform = normalTextureUniform
          AmbientOcclusionTextureUniform = ambientOcclusionTextureUniform
          IrradianceMapUniform = irradianceMapUniform
          EnvironmentFilterMapUniform = environmentFilterMapUniform
          BrdfTextureUniform = brdfTextureUniform
          LightPositionsUniform = lightPositionsUniform
          LightBrightnessesUniform = lightBrightnessesUniform
          LightIntensitiesUniform = lightIntensitiesUniform
          LightColorsUniform = lightColorsUniform
          PhysicallyBasedShader = shader }

    let CreatePhysicallyBasedDeferred2Shader (shaderFilePath : string) =

        // create shader
        let shader = Shader.CreateShaderFromFilePath shaderFilePath

        // retrieve uniforms
        let eyePositionUniform = Gl.GetUniformLocation (shader, "eyePosition")
        let positionTextureUniform = Gl.GetUniformLocation (shader, "positionTexture")
        let normalTextureUniform = Gl.GetUniformLocation (shader, "normalTexture")
        let albedoTextureUniform = Gl.GetUniformLocation (shader, "albedoTexture")
        let materialTextureUniform = Gl.GetUniformLocation (shader, "materialTexture")
        let irradianceMapUniform = Gl.GetUniformLocation (shader, "irradianceMap")
        let environmentFilterMapUniform = Gl.GetUniformLocation (shader, "environmentFilterMap")
        let brdfTextureUniform = Gl.GetUniformLocation (shader, "brdfTexture")
        let lightPositionsUniform = Gl.GetUniformLocation (shader, "lightPositions")
        let lightBrightnessesUniform = Gl.GetUniformLocation (shader, "lightBrightnesses")
        let lightIntensitiesUniform = Gl.GetUniformLocation (shader, "lightIntensities")
        let lightColorsUniform = Gl.GetUniformLocation (shader, "lightColors")

        // make shader record
        { EyePositionUniform = eyePositionUniform
          PositionTextureUniform = positionTextureUniform
          NormalTextureUniform = normalTextureUniform
          AlbedoTextureUniform = albedoTextureUniform
          MaterialTextureUniform = materialTextureUniform
          IrradianceMapUniform = irradianceMapUniform
          EnvironmentFilterMapUniform = environmentFilterMapUniform
          BrdfTextureUniform = brdfTextureUniform
          LightPositionsUniform = lightPositionsUniform
          LightBrightnessesUniform = lightBrightnessesUniform
          LightIntensitiesUniform = lightIntensitiesUniform
          LightColorsUniform = lightColorsUniform
          PhysicallyBasedDeferred2Shader = shader }

    let CreatePhysicallyBasedDeferredShaders (shaderFilePath, shader2FilePath) =
        let shader = CreatePhysicallyBasedShader shaderFilePath // deferred shader 1 uses the same API as physically based shader
        let shader2 = CreatePhysicallyBasedDeferred2Shader shader2FilePath
        (shader, shader2)

    /// Draw a batch of physically-based surfaces.
    let DrawPhysicallyBasedSurfaces
        (eyePosition : Vector3,
         modelsFields : single array,
         modelsCount : int,
         view : single array,
         projection : single array,
         blending,
         irradianceMap : uint,
         environmentFilterMap : uint,
         brdfTexture : uint,
         lightPositions : single array,
         lightColors : single array,
         lightBrightnesses : single array,
         lightIntensities : single array,
         material : PhysicallyBasedMaterial,
         geometry : PhysicallyBasedGeometry,
         shader : PhysicallyBasedShader) =

        // setup state
        Gl.Enable (LanguagePrimitives.EnumOfValue OpenGL.Gl.TEXTURE_CUBE_MAP_SEAMLESS)
        Gl.DepthMask true
        Gl.DepthFunc DepthFunction.Lequal
        Gl.Enable EnableCap.DepthTest
        if blending then
            Gl.BlendEquation BlendEquationMode.FuncAdd
            Gl.BlendFunc (BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha)
            Gl.Enable EnableCap.Blend
        Gl.Enable EnableCap.CullFace
        Hl.Assert ()

        // setup shader
        Gl.UseProgram shader.PhysicallyBasedShader
        Gl.UniformMatrix4 (shader.ViewUniform, false, view)
        Gl.UniformMatrix4 (shader.ProjectionUniform, false, projection)
        Gl.Uniform3 (shader.EyePositionUniform, eyePosition.X, eyePosition.Y, eyePosition.Z)
        Gl.Uniform1 (shader.AlbedoTextureUniform, 0)
        Gl.Uniform1 (shader.MetalnessTextureUniform, 1)
        Gl.Uniform1 (shader.RoughnessTextureUniform, 2)
        Gl.Uniform1 (shader.NormalTextureUniform, 3)
        Gl.Uniform1 (shader.AmbientOcclusionTextureUniform, 4)
        Gl.Uniform1 (shader.IrradianceMapUniform, 5)
        Gl.Uniform1 (shader.EnvironmentFilterMapUniform, 6)
        Gl.Uniform1 (shader.BrdfTextureUniform, 7)
        Gl.Uniform3 (shader.LightPositionsUniform, lightPositions)
        Gl.Uniform1 (shader.LightBrightnessesUniform, lightBrightnesses)
        Gl.Uniform1 (shader.LightIntensitiesUniform, lightIntensities)
        Gl.Uniform4 (shader.LightColorsUniform, lightColors)
        Gl.ActiveTexture TextureUnit.Texture0
        Gl.BindTexture (TextureTarget.Texture2d, material.AlbedoTexture)
        Gl.ActiveTexture TextureUnit.Texture1
        Gl.BindTexture (TextureTarget.Texture2d, material.MetalnessTexture)
        Gl.ActiveTexture TextureUnit.Texture2
        Gl.BindTexture (TextureTarget.Texture2d, material.RoughnessTexture)
        Gl.ActiveTexture TextureUnit.Texture3
        Gl.BindTexture (TextureTarget.Texture2d, material.NormalTexture)
        Gl.ActiveTexture TextureUnit.Texture4
        Gl.BindTexture (TextureTarget.Texture2d, material.AmbientOcclusionTexture)
        Gl.ActiveTexture TextureUnit.Texture5
        Gl.BindTexture (TextureTarget.TextureCubeMap, irradianceMap)
        Gl.ActiveTexture TextureUnit.Texture6
        Gl.BindTexture (TextureTarget.TextureCubeMap, environmentFilterMap)
        Gl.ActiveTexture TextureUnit.Texture7
        Gl.BindTexture (TextureTarget.Texture2d, brdfTexture)
        Hl.Assert ()

        // update models buffer
        let modelsFieldsPtr = GCHandle.Alloc (modelsFields, GCHandleType.Pinned)
        try Gl.BindBuffer (BufferTarget.ArrayBuffer, geometry.ModelBuffer)
            Gl.BufferData (BufferTarget.ArrayBuffer, uint (modelsCount * 16 * sizeof<single>), modelsFieldsPtr.AddrOfPinnedObject (), BufferUsage.DynamicDraw)
        finally modelsFieldsPtr.Free ()
        Gl.BindBuffer (BufferTarget.ArrayBuffer, 0u)
        Hl.Assert ()

        // setup geometry
        Gl.BindVertexArray geometry.PhysicallyBasedVao
        Gl.BindBuffer (BufferTarget.ArrayBuffer, geometry.VertexBuffer)
        Gl.BindBuffer (BufferTarget.ElementArrayBuffer, geometry.IndexBuffer)
        Hl.Assert ()

        // draw geometry
        Gl.DrawElementsInstanced (geometry.PrimitiveType, geometry.ElementCount, DrawElementsType.UnsignedInt, nativeint 0, modelsCount)
        Hl.Assert ()

        // teardown geometry
        Gl.BindVertexArray 0u
        Hl.Assert ()

        // teardown shader
        Gl.ActiveTexture TextureUnit.Texture0
        Gl.BindTexture (TextureTarget.Texture2d, 0u)
        Gl.ActiveTexture TextureUnit.Texture1
        Gl.BindTexture (TextureTarget.Texture2d, 0u)
        Gl.ActiveTexture TextureUnit.Texture2
        Gl.BindTexture (TextureTarget.Texture2d, 0u)
        Gl.ActiveTexture TextureUnit.Texture3
        Gl.BindTexture (TextureTarget.Texture2d, 0u)
        Gl.ActiveTexture TextureUnit.Texture4
        Gl.BindTexture (TextureTarget.Texture2d, 0u)
        Gl.ActiveTexture TextureUnit.Texture5
        Gl.BindTexture (TextureTarget.TextureCubeMap, 0u)
        Gl.ActiveTexture TextureUnit.Texture6
        Gl.BindTexture (TextureTarget.TextureCubeMap, 0u)
        Gl.ActiveTexture TextureUnit.Texture7
        Gl.BindTexture (TextureTarget.Texture2d, 0u)
        Gl.UseProgram 0u
        Hl.Assert ()

        // teardown state
        Gl.Disable EnableCap.CullFace
        if blending then
            Gl.Disable EnableCap.Blend
            Gl.BlendFunc (BlendingFactor.One, BlendingFactor.Zero)
            Gl.BlendEquation BlendEquationMode.FuncAdd
        Gl.Disable EnableCap.DepthTest
        Gl.DepthFunc DepthFunction.Less
        Gl.DepthMask false
        Gl.Disable (LanguagePrimitives.EnumOfValue OpenGL.Gl.TEXTURE_CUBE_MAP_SEAMLESS)

    /// Draw a the second pass of a deferred physically-based surface.
    let DrawPhysicallyBasedDeferred2Surface
        (eyePosition : Vector3,
         positionTexture : uint,
         normalTexture : uint,
         albedoTexture : uint,
         materialTexture : uint,
         irradianceMap : uint,
         environmentFilterMap : uint,
         brdfTexture : uint,
         lightPositions : single array,
         lightColors : single array,
         lightBrightnesses : single array,
         lightIntensities : single array,
         geometry : PhysicallyBasedGeometry,
         shader : PhysicallyBasedDeferred2Shader) =

        // setup state
        Gl.Enable (LanguagePrimitives.EnumOfValue OpenGL.Gl.TEXTURE_CUBE_MAP_SEAMLESS)

        // setup shader
        Gl.UseProgram shader.PhysicallyBasedDeferred2Shader
        Gl.Uniform3 (shader.EyePositionUniform, eyePosition.X, eyePosition.Y, eyePosition.Z)
        Gl.Uniform1 (shader.PositionTextureUniform, 0)
        Gl.Uniform1 (shader.NormalTextureUniform, 1)
        Gl.Uniform1 (shader.AlbedoTextureUniform, 2)
        Gl.Uniform1 (shader.MaterialTextureUniform, 3)
        Gl.Uniform1 (shader.IrradianceMapUniform, 4)
        Gl.Uniform1 (shader.EnvironmentFilterMapUniform, 5)
        Gl.Uniform1 (shader.BrdfTextureUniform, 6)
        Gl.Uniform3 (shader.LightPositionsUniform, lightPositions)
        Gl.Uniform1 (shader.LightBrightnessesUniform, lightBrightnesses)
        Gl.Uniform1 (shader.LightIntensitiesUniform, lightIntensities)
        Gl.Uniform4 (shader.LightColorsUniform, lightColors)
        Gl.ActiveTexture TextureUnit.Texture0
        Gl.BindTexture (TextureTarget.Texture2d, positionTexture)
        Gl.ActiveTexture TextureUnit.Texture1
        Gl.BindTexture (TextureTarget.Texture2d, normalTexture)
        Gl.ActiveTexture TextureUnit.Texture2
        Gl.BindTexture (TextureTarget.Texture2d, albedoTexture)
        Gl.ActiveTexture TextureUnit.Texture3
        Gl.BindTexture (TextureTarget.Texture2d, materialTexture)
        Gl.ActiveTexture TextureUnit.Texture4
        Gl.BindTexture (TextureTarget.TextureCubeMap, irradianceMap)
        Gl.ActiveTexture TextureUnit.Texture5
        Gl.BindTexture (TextureTarget.TextureCubeMap, environmentFilterMap)
        Gl.ActiveTexture TextureUnit.Texture6
        Gl.BindTexture (TextureTarget.Texture2d, brdfTexture)
        Hl.Assert ()

        // setup geometry
        Gl.BindVertexArray geometry.PhysicallyBasedVao
        Gl.BindBuffer (BufferTarget.ArrayBuffer, geometry.VertexBuffer)
        Gl.BindBuffer (BufferTarget.ElementArrayBuffer, geometry.IndexBuffer)
        Hl.Assert ()

        // draw geometry
        Gl.DrawElements (geometry.PrimitiveType, geometry.ElementCount, DrawElementsType.UnsignedInt, nativeint 0)
        Hl.Assert ()

        // teardown geometry
        Gl.BindVertexArray 0u
        Hl.Assert ()

        // teardown shader
        Gl.ActiveTexture TextureUnit.Texture0
        Gl.BindTexture (TextureTarget.Texture2d, 0u)
        Gl.ActiveTexture TextureUnit.Texture1
        Gl.BindTexture (TextureTarget.Texture2d, 0u)
        Gl.ActiveTexture TextureUnit.Texture2
        Gl.BindTexture (TextureTarget.Texture2d, 0u)
        Gl.ActiveTexture TextureUnit.Texture3
        Gl.BindTexture (TextureTarget.Texture2d, 0u)
        Gl.ActiveTexture TextureUnit.Texture4
        Gl.BindTexture (TextureTarget.TextureCubeMap, 0u)
        Gl.ActiveTexture TextureUnit.Texture5
        Gl.BindTexture (TextureTarget.TextureCubeMap, 0u)
        Gl.ActiveTexture TextureUnit.Texture6
        Gl.BindTexture (TextureTarget.Texture2d, 0u)
        Gl.UseProgram 0u

        // teardown state
        Gl.Disable (LanguagePrimitives.EnumOfValue OpenGL.Gl.TEXTURE_CUBE_MAP_SEAMLESS)