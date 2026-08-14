using UnityEngine;
using Unity.Collections;
using BuildingVolumes.Player;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Rendering;

//A meshlet variant of the Shadergraph pointcloud renderer for standard platforms.
//It renders through the same Shadergraph compute + materials as PointcloudRendererRT,
//but splits the cloud into copies of the baked Meshlet prefab that are instantiated
//incrementally over several frames, avoiding the single large mesh allocation hitch.
//Unlike PointcloudRendererRT_Meshlet it carries none of the Polyspatial/AVP specifics
//(no MarkDirty, no startup stabilization waits).

namespace BuildingVolumes.Player
{
  public class PointcloudRendererRT_MeshletSG : MonoBehaviour, IPointCloudRenderer
  {
    ComputeShader computeShaderRT;
    RenderTexture rtPositions;
    RenderTexture rtColors;
    int rtResolution;

    //Triple buffering neccessary, as not all dispatches are guranteed
    //to perform in one frame
    GraphicsBuffer[] pointSourceBuffers = new GraphicsBuffer[3];
    int bufferIndex = 0;

    float currentPointSize = 0;
    float currentPointEmission = 1;

    //Quads per meshlet mesh. Derived from the baked Meshlet prefab mesh in Setup()
    //so the _VertexIDOffset handed to the shader always matches the actual vertex
    //layout; a hardcoded value here silently breaks if the prefab mesh is re-baked.
    int meshletQuadCount = 2000;

    GameObject pcRenderParent;
    List<GameObject> meshObjects;
    List<MeshFilter> meshFilters;
    List<MeshRenderer> meshRenderers;

    GameObject meshletPrefab;

    bool ready = true;
    bool isDisposed;

    //Compute shader property IDs
    static readonly int pointSourceBufferID = Shader.PropertyToID("_PointSourceBuffer");
    static readonly int pointSourceStrideID = Shader.PropertyToID("_SourceStride");
    static readonly int pointCountID = Shader.PropertyToID("_PointCount");
    static readonly int rtPositionsID = Shader.PropertyToID("_RTPositions");
    static readonly int rtColorsID = Shader.PropertyToID("_RTColors");
    static readonly int rtStrideID = Shader.PropertyToID("_RTStride");
    static readonly int rtNormalsEnabledID = Shader.PropertyToID("_RTHasNormals");

    //Vertex/Fragment shader property IDs
    static readonly int rtResolutionID = Shader.PropertyToID("_RTResolution");
    static readonly int rtVertexOffsetID = Shader.PropertyToID("_VertexIDOffset");
    static readonly int rtPositionSourceID = Shader.PropertyToID("_PositionSourceRT");
    static readonly int rtColorSourceID = Shader.PropertyToID("_ColorSourceRT");
    static readonly int pointScaleID = Shader.PropertyToID("_PointScale");
    static readonly int pointEmissionID = Shader.PropertyToID("_PointEmission");

    /// <summary>
    /// Prepare all the buffers for a pointcloud sequence. Only needs to bet set once per sequence
    /// </summary>
    public void Setup(SequenceConfiguration configuration, Transform parent, float pointSize, float pointEmission, Material mat, bool instantiateMaterial)
    {
      Dispose();

      if (configuration.hasNormals)
      {
        Debug.LogError("Pointcloud sequences with normals are not supported by the Shadergraph Meshlet renderer!");
        return;
      }

      ready = true;
      isDisposed = false;
      currentPointSize = pointSize;
      currentPointEmission = pointEmission;

      pcRenderParent = CreateStreamObject("PointcloudRenderer", parent);

      if (computeShaderRT == null)
        computeShaderRT = Resources.Load("ShaderGraph/Pointcloud_Shadergraph", typeof(ComputeShader)) as ComputeShader;

      if (meshletPrefab == null)
        meshletPrefab = Resources.Load("Meshlet") as GameObject;

      Mesh prefabMesh = meshletPrefab.GetComponent<MeshFilter>().sharedMesh;
      meshletQuadCount = prefabMesh.vertexCount / 4;

      //Calculate a square shaped texture in which all point data will fit
      rtResolution = Mathf.CeilToInt(Mathf.Sqrt(configuration.maxVertexCount));

      //Render Texture for storing point positions
      rtPositions = new RenderTexture(rtResolution, rtResolution, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat);
      rtPositions.enableRandomWrite = true;
      rtPositions.filterMode = FilterMode.Point;
      rtPositions.Create();

      //Render texture for storing colors
      rtColors = new RenderTexture(rtResolution, rtResolution, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm);
      rtColors.enableRandomWrite = true;
      rtColors.filterMode = FilterMode.Point;
      rtColors.Create();

      //Create the buffer where all the raw point data will be stored
      int textureSize = rtPositions.width * rtPositions.height;
      int stride = 4 * 4;

      for (int i = 0; i < pointSourceBuffers.Length; i++)
      {
        pointSourceBuffers[i] = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.LockBufferForWrite, textureSize, stride);
      }

      computeShaderRT.SetInt(rtStrideID, rtResolution);
      computeShaderRT.SetInt(pointSourceStrideID, stride);
      computeShaderRT.SetBool(rtNormalsEnabledID, false);
      computeShaderRT.SetTexture(0, rtPositionsID, rtPositions);
      computeShaderRT.SetTexture(0, rtColorsID, rtColors);

      //Create the pointcloud meshlets. Spread over multiple frames in play mode,
      //but built synchronously in edit mode (thumbnail previews) where coroutines don't tick.
      if (Application.isPlaying)
        StartCoroutine(MeshCreation(configuration, pointSize, pointEmission, mat, instantiateMaterial));
      else
        MeshCreationImmediate(configuration, pointSize, pointEmission, mat, instantiateMaterial);
    }

    /// <summary>
    /// Instantiate the meshlets over multiple frames so a large cloud doesn't stall on setup.
    /// </summary>
    IEnumerator MeshCreation(SequenceConfiguration config, float pointSize, float pointEmission, Material mat, bool instantiateMaterial)
    {
      int meshPartCount = Mathf.CeilToInt((float)config.maxVertexCount / meshletQuadCount);

      InitMeshletLists();

      for (int j = 0; j < meshPartCount; j++)
      {
        CreateMeshlet(config, j, mat, pointSize, pointEmission, instantiateMaterial);
        yield return null;
      }

      ready = true;
    }

    void MeshCreationImmediate(SequenceConfiguration config, float pointSize, float pointEmission, Material mat, bool instantiateMaterial)
    {
      int meshPartCount = Mathf.CeilToInt((float)config.maxVertexCount / meshletQuadCount);

      InitMeshletLists();

      for (int j = 0; j < meshPartCount; j++)
        CreateMeshlet(config, j, mat, pointSize, pointEmission, instantiateMaterial);

      ready = true;
    }

    void InitMeshletLists()
    {
      meshObjects = new List<GameObject>();
      meshFilters = new List<MeshFilter>();
      meshRenderers = new List<MeshRenderer>();
    }

    void CreateMeshlet(SequenceConfiguration config, int meshletIndex, Material mat, float pointSize, float pointEmission, bool instantiateMaterial)
    {
      GameObject newMeshlet = Instantiate(meshletPrefab, pcRenderParent.transform);

      MeshRenderer meshRenderer = newMeshlet.GetComponent<MeshRenderer>();
      MeshFilter meshFilter = newMeshlet.GetComponent<MeshFilter>();
      meshFilters.Add(meshFilter);
      meshRenderers.Add(meshRenderer);
      meshObjects.Add(newMeshlet);

      meshFilter.sharedMesh.bounds = config.GetBounds();
      SetMaterial(meshRenderer, mat, meshletIndex, pointSize, pointEmission, instantiateMaterial);
    }

    /// <summary>
    /// Update the pointcloud data in the sequence with a new pointcloud frame.
    /// </summary>
    public void SetFrame(Frame frame)
    {
      if (!ready || isDisposed)
        return;

      bufferIndex++;
      if (bufferIndex >= 3)
        bufferIndex = 0;

      frame.geoJobHandle.Complete();
      frame.decompressionJobHandle.Complete();
      NativeArray<byte> pointdataGPU = pointSourceBuffers[bufferIndex].LockBufferForWrite<byte>(0, frame.geoJob.vertexBuffer.Length); //Locking buffer is faster than GraphicsBuffer.SetData;
      frame.geoJob.vertexBuffer.CopyTo(pointdataGPU);
      pointSourceBuffers[bufferIndex].UnlockBufferAfterWrite<byte>(frame.geoJob.vertexBuffer.Length);
      computeShaderRT.SetInt(pointCountID, frame.geoJob.vertexCount);

      int groupSize = Mathf.CeilToInt(rtPositions.width / 32f);
      computeShaderRT.SetBuffer(0, pointSourceBufferID, pointSourceBuffers[bufferIndex]);
      computeShaderRT.Dispatch(0, groupSize, groupSize, 1);
    }

    public void SetPointcloudMaterial(Material mat, bool instantiateMaterial)
    {
      SetPointcloudMaterial(mat, currentPointSize, currentPointEmission, instantiateMaterial);
    }

    public void SetPointcloudMaterial(Material mat, float pointSize, float pointEmission, bool instantiateMaterial)
    {
      if (meshRenderers == null)
        return;

      for (int i = 0; i < meshRenderers.Count; i++)
      {
        if (meshRenderers[i] != null)
          SetMaterial(meshRenderers[i], mat, i, pointSize, pointEmission, instantiateMaterial);
      }
    }

    void SetMaterial(MeshRenderer renderer, Material mat, int meshletIndex, float pointSize, float pointEmission, bool instantiateMaterial)
    {
      if (!mat)
        mat = LoadDefaultMaterial();

      currentPointSize = pointSize;
      currentPointEmission = pointEmission;

      Material newMat;

      if (instantiateMaterial)
        newMat = new Material(mat);
      else
        newMat = mat;

      newMat.SetFloat(rtResolutionID, rtResolution);
      newMat.SetFloat(rtVertexOffsetID, meshletIndex * meshletQuadCount * 4);
      newMat.SetTexture(rtPositionSourceID, rtPositions);
      newMat.SetTexture(rtColorSourceID, rtColors);

      if (newMat.HasFloat(pointScaleID))
        newMat.SetFloat(pointScaleID, pointSize);

      if (newMat.HasFloat(pointEmissionID))
        newMat.SetFloat(pointEmissionID, pointEmission);

      if (renderer != null)
        renderer.sharedMaterial = newMat;
    }

    public void Show()
    {
      if (meshRenderers == null)
        return;

      for (int i = 0; i < meshRenderers.Count; i++)
      {
        if (meshRenderers[i] != null)
          meshRenderers[i].enabled = true;
      }
    }

    public void Hide()
    {
      if (meshRenderers == null)
        return;

      for (int i = 0; i < meshRenderers.Count; i++)
      {
        if (meshRenderers[i] != null)
          meshRenderers[i].enabled = false;
      }
    }

    GameObject CreateStreamObject(string name, Transform parent)
    {
      GameObject newStreamObject = new GameObject(name);
      newStreamObject.transform.parent = parent;
      newStreamObject.transform.localPosition = Vector3.zero;
      newStreamObject.transform.localRotation = Quaternion.identity;
      newStreamObject.transform.localScale = Vector3.one;
      newStreamObject.hideFlags = HideFlags.DontSave;
      return newStreamObject;
    }

    public Material LoadDefaultMaterial()
    {
      //Built from the shader directly (not a .mat) so this render path needs no
      //dedicated material asset. This graph is the circles shader with the extra
      //_VertexIDOffset input the meshlet split requires.
      Shader shader = Resources.Load("ShaderGraph/Pointcloud_Circles_Meshlet_Shadergraph", typeof(Shader)) as Shader;

      if (shader == null)
      {
        UnityEngine.Debug.LogError("Pointcloud Circles Meshlet shader (Shadergraph) could not be loaded!");
        return null;
      }

      return new Material(shader);
    }

    public void SetPointSize(float size)
    {
      currentPointSize = size;

      if (meshRenderers == null)
        return;

      for (int i = 0; i < meshRenderers.Count; i++)
      {
        if (meshRenderers[i] != null && meshRenderers[i].sharedMaterial.HasFloat(pointScaleID))
          meshRenderers[i].sharedMaterial.SetFloat(pointScaleID, size);
      }
    }

    public void SetPointEmission(float emission)
    {
      currentPointEmission = emission;

      if (meshRenderers == null)
        return;

      for (int i = 0; i < meshRenderers.Count; i++)
      {
        if (meshRenderers[i] != null && meshRenderers[i].sharedMaterial.HasFloat(pointEmissionID))
          meshRenderers[i].sharedMaterial.SetFloat(pointEmissionID, emission);
      }
    }

    public void Dispose()
    {
      if (rtPositions != null)
        DestroyImmediate(rtPositions);
      if (rtColors != null)
        DestroyImmediate(rtColors);
      for (int i = 0; i < pointSourceBuffers.Length; i++)
      {
        if (pointSourceBuffers[i] != null)
          pointSourceBuffers[i].Dispose();
      }

      if (meshFilters != null)
      {
        for (int i = 0; i < meshFilters.Count; i++)
        {
          if (meshFilters[i] != null)
            DestroyImmediate(meshFilters[i]);
        }

        meshFilters.Clear();
        meshFilters = null;
      }

      if (meshRenderers != null)
      {
        for (int i = 0; i < meshRenderers.Count; i++)
        {
          if (meshRenderers[i] != null)
            DestroyImmediate(meshRenderers[i]);
        }

        meshRenderers.Clear();
        meshRenderers = null;
      }

      if (meshObjects != null)
      {
        for (int i = 0; i < meshObjects.Count; i++)
        {
          if (meshObjects[i] != null)
            DestroyImmediate(meshObjects[i]);
        }

        meshObjects.Clear();
        meshObjects = null;
      }

      if (pcRenderParent != null)
        DestroyImmediate(pcRenderParent);

      isDisposed = true;
    }

    public bool IsDisposed()
    {
      return isDisposed;
    }

  }

}
