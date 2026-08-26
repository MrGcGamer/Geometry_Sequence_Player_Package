using System.Collections;
using UnityEngine;

namespace BuildingVolumes.Player
{
  /// <summary>
  /// The meshlet render path for Unity Polyspatial on the Apple Vision Pro. Mesh creation is
  /// expensive enough there to crash the device if it all happens at once, so the cloud is split
  /// into copies of the baked Meshlet prefab that are instantiated over many frames, with the
  /// frame time allowed to recover in between.
  /// </summary>
  public class PointcloudRendererRT_Meshlet : PointcloudRendererRTBase
  {
    static readonly int rtVertexOffsetID = Shader.PropertyToID("_VertexIDOffset");

    GameObject meshletPrefab;

    //Quads per meshlet mesh. Read off the baked prefab mesh rather than hardcoded, so the
    //_VertexIDOffset handed to the shader always matches the actual vertex layout; a constant here
    //breaks silently when the prefab mesh is re-baked.
    int meshletQuadCount;

    protected override string StreamObjectName => "PointcloudRenderer";
    protected override string ComputeShaderResourcePath => "PolySpatial/Pointcloud_Polyspatial";

    //One source buffer instead of the usual three: on the AVP the memory this saves is worth more
    //than the overlap between a dispatch and the next frame's upload.
    protected override int SourceBufferCount => 1;

    protected override bool NeedsMaterialPerRenderer => true;

    protected override string GetUnsupportedReason(SequenceConfiguration config)
    {
      return config.hasNormals ? "Pointcloud sequences with normals are not supported on Polyspatial!" : null;
    }

    protected override void CreateGeometry(SequenceConfiguration config)
    {
      if (LoadMeshletPrefab())
        StartCoroutine(CreateMeshlets(config));
    }

    /// <summary>Loads the meshlet prefab and reads its quad count off it. False if it is missing.</summary>
    bool LoadMeshletPrefab()
    {
      if (meshletPrefab == null)
        meshletPrefab = Resources.Load("Meshlet") as GameObject;

      if (meshletPrefab == null)
      {
        Debug.LogError("Could not load the meshlet prefab at Resources/Meshlet");
        return false;
      }

      meshletQuadCount = meshletPrefab.GetComponent<MeshFilter>().sharedMesh.vertexCount / 4;
      return meshletQuadCount > 0;
    }

    IEnumerator CreateMeshlets(SequenceConfiguration config)
    {
      //Wait a few seconds when the app has just started, otherwise we risk crashing polyspatial
      if (Time.time < 3f && Application.isPlaying)
        yield return new WaitForSeconds(3f - Time.time);

      int meshletCount = Mathf.CeilToInt((float)config.maxVertexCount / meshletQuadCount);

      for (int i = 0; i < meshletCount; i++)
      {
        if (!CreateMeshlet())
          yield break;

        //Two stabilizer passes per meshlet: instantiating one and giving it its material each cost
        //Polyspatial a frame's worth of recovery, and this path shipped waiting for both.
        if (Application.isPlaying)
        {
          yield return StartCoroutine(DeltaTimeStabilizer());
          yield return StartCoroutine(DeltaTimeStabilizer());
        }
      }
    }

    /// <summary>
    /// Hold until the frame time has been healthy for ten consecutive frames, or three seconds have
    /// passed. Polyspatial keeps processing an added meshlet well after the call returns.
    /// </summary>
    IEnumerator DeltaTimeStabilizer()
    {
      const float timeout = 3f;
      float timeAtBeginning = Time.time;
      int stabilizedFrames = 0;

      yield return null;

      while (stabilizedFrames < 10)
      {
        if (Time.deltaTime < 0.033f)
          stabilizedFrames++;

        if (Time.time - timeAtBeginning > timeout)
          break;

        yield return null;
      }
    }

    /// <summary>
    /// Instantiate one meshlet under the stream object and register its renderer. Returns false once
    /// there is nothing left to build under, which is how CreateMeshlets notices a Dispose that
    /// happened while it was still spreading meshlets over frames.
    /// </summary>
    bool CreateMeshlet()
    {
      if (isDisposed || pcObject == null)
        return false;

      GameObject meshlet = Instantiate(meshletPrefab, pcObject.transform);
      MeshFilter meshFilter = meshlet.GetComponent<MeshFilter>();

      //The prefab's mesh is a shared asset, so this widens the bounds for every meshlet at once -
      //and is also why it must not be registered as an owned mesh.
      if (meshFilter != null && meshFilter.sharedMesh != null)
        meshFilter.sharedMesh.bounds = configuration.GetBounds();

      RegisterRenderer(meshlet.GetComponent<MeshRenderer>());
      return true;
    }

    protected override void ConfigureMaterial(Material mat, int rendererIndex)
    {
      base.ConfigureMaterial(mat, rendererIndex);

      //Meshlets are registered in order, so the renderer index is the meshlet index, and the shader
      //derives each meshlet's slice of the point textures from the vertex offset it implies.
      mat.SetFloat(rtVertexOffsetID, rendererIndex * meshletQuadCount * 4);
    }

    protected override int PrepareUpload(int pointCount)
    {
      //Every meshlet draws the same shared prefab mesh, so the draw cannot be clamped per meshlet
      //the way the single-mesh path clamps its submesh. Whole meshlets are switched off instead,
      //which is why the saving lands on meshlet granularity rather than on the exact point count.
      int drawnMeshlets = Mathf.CeilToInt(pointCount / (float)meshletQuadCount);
      SetDrawnRendererCount(drawnMeshlets);

      //Counted to the end of the last drawn meshlet, not to pointCount: that meshlet still draws its
      //full quad count, and the compute pass has to reach the texels past the frame's points to zero
      //their alpha. Stopping at pointCount would leave the previous frame's points showing there.
      int coveredPoints = Mathf.Min(drawnMeshlets * meshletQuadCount, rtResolution * rtResolution);
      return Mathf.CeilToInt(coveredPoints / (float)rtResolution);
    }

    protected override void OnFrameUploaded()
    {
#if UNITY_VISIONOS && INCLUDE_UNITY_POLYSPATIAL
      Unity.PolySpatial.PolySpatialObjectUtils.MarkDirty(rtPositions);
      Unity.PolySpatial.PolySpatialObjectUtils.MarkDirty(rtColors);
#endif
    }

    protected override Material LoadDefaultMaterial()
    {
      Material mat = Resources.Load("PolySpatial/Pointcloud_Circles_Polyspatial", typeof(Material)) as Material;

      if (mat == null)
        Debug.LogError("Pointcloud Circles material (Polyspatial) could not be loaded!");

      return mat;
    }
  }
}
