using UnityEngine;
using Unity.Collections;
using BuildingVolumes.Player;
using System.Collections;
using System.Collections.Generic;

//This is a special version of the pointcloud renderer,
//that should only really be used for Unity Polyspatial on the Apple Vision Pro
//It builds the mesh much more slowly than the standard variant of the shader
//and distributes the load over multiple meshes and frames

namespace BuildingVolumes.Player
{
  public class PointcloudRendererRT_Meshlet : MonoBehaviour, IPointCloudRenderer
  {
    ComputeShader computeShaderRT;
    RenderTexture rtPositions;
    RenderTexture rtColors;
    int rtResolution;
    float currentPointSize = 0;
    float currentPointEmission = 1;
    //Quads per meshlet mesh. Derived from the baked Meshlet prefab mesh in Setup()
    //so the _VertexIDOffset handed to the shader always matches the actual vertex
    //layout; a hardcoded value here silently breaks if the prefab mesh is re-baked.
    int meshletQuadCount = 2000;


    GraphicsBuffer pointSourceBuffer;

    GameObject pcRenderParent;
    List<GameObject> meshObjects;
    List<MeshFilter> meshFilters;
    List<MeshRenderer> meshRenderers;

    GameObject meshletPrefab;

    bool ready = true;
    bool isDisposed;

    //Compute shader property IDs
    static readonly int pointSourceBufferID = Shader.PropertyToID("_PointSourceBuffer");
    static readonly int pointCountID = Shader.PropertyToID("_PointCount");
    static readonly int rtPositionsID = Shader.PropertyToID("_RTPositions");
    static readonly int rtColorsID = Shader.PropertyToID("_RTColors");
    static readonly int rtStrideID = Shader.PropertyToID("_RTStride");

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
    /// <param name="maxPointCount">The maximum number of points that could appear in any frame of the sequence</param>
    /// <param name="meshFilter">The meshfilter where the point geometry data will be rendered into</param>
    /// <param name="meshRenderer">The meshrenderer used for rendering the points. Will be auto-configured</param>
    public void Setup(SequenceConfiguration configuration, Transform parent, float pointSize, float pointEmission, Material mat, bool instantiateMaterial)
    {
      Dispose();

      if (configuration.hasNormals)
      {
        Debug.LogError("Pointcloud sequences with normals are not supported on Polyspatial!");
        return;
      }

      pcRenderParent = CreateStreamObject("PointcloudRenderer", parent);

      ready = true;
      currentPointSize = pointSize;
      currentPointEmission = pointEmission;

      if (computeShaderRT == null)
        computeShaderRT = Resources.Load("PolySpatial/Pointcloud_Polyspatial", typeof(ComputeShader)) as ComputeShader;

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
      pointSourceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.LockBufferForWrite, textureSize, 4 * 4);

      computeShaderRT.SetInt(rtStrideID, rtResolution);
      computeShaderRT.SetBuffer(0, pointSourceBufferID, pointSourceBuffer);
      computeShaderRT.SetTexture(0, rtPositionsID, rtPositions);
      computeShaderRT.SetTexture(0, rtColorsID, rtColors);

      isDisposed = false;

      //Create the pointcloud mesh with n points
      StartCoroutine(MeshCreation(configuration, pointSize, pointEmission, mat, instantiateMaterial));
    }

    /// <summary>
    /// On Polyspatial, mesh creation is a relatively expensive process
    /// We therefore need to slowly create the mesh over multiple frames
    /// and also distribute the mesh over multiple Meshfilters.
    /// Otherwise, we risk fatal crashes where the AVP needs to restart
    /// </summary>
    IEnumerator MeshCreation(SequenceConfiguration config, float pointSize, float pointEmission, Material mat, bool instantiateMaterial)
    {
      //Wait a few seconds when the app has just started, otherwise we risk crashing polyspatial
      if (Time.time < 3f && Application.isPlaying)
        yield return new WaitForSeconds(3f - Time.time);

      int meshPartCount = Mathf.CeilToInt((float)config.maxVertexCount / meshletQuadCount);

      meshObjects = new List<GameObject>();
      meshFilters = new List<MeshFilter>();
      meshRenderers = new List<MeshRenderer>();

      for (int j = 0; j < meshPartCount; j++)
      {

        GameObject newMeshlet = Instantiate(meshletPrefab, pcRenderParent.transform);

        if (Application.isPlaying)
          yield return StartCoroutine(DeltaTimeStabilizer("Meshlet added"));

        MeshRenderer meshRenderer = newMeshlet.GetComponent<MeshRenderer>();
        MeshFilter meshFilter = newMeshlet.GetComponent<MeshFilter>();
        meshFilters.Add(meshFilter);
        meshRenderers.Add(meshRenderer);
        meshObjects.Add(newMeshlet);

        meshFilter.sharedMesh.bounds = config.GetBounds();
        SetMaterial(meshRenderer, mat, j, pointSize, pointEmission, instantiateMaterial);

        if (Application.isPlaying)
          yield return StartCoroutine(DeltaTimeStabilizer("Meshlet added"));
      }

      ready = true;

    }

    IEnumerator DeltaTimeStabilizer(string action)
    {
      int deltaTimeStabilizedCounter = 0;
      float timeout = 3f;
      float timeAtBeginning = Time.time;

      int framesToStabilize = 0;

      if (Application.isPlaying)
      {
        yield return null;

        while (deltaTimeStabilizedCounter < 10)
        {
          if (Time.deltaTime < 0.033f)
            deltaTimeStabilizedCounter++;

          if (Time.time - timeAtBeginning > timeout)
            break;

          framesToStabilize++;
          yield return null;
        }

      }


    }
    /// <summary>
    /// Update the pointcloud data in the sequence with a new pointcloud frame.
    /// </summary>
    /// <param name="pointSource">A native buffer of points with their colors and positions</param>
    /// <param name="pointCount">The number of points in the current frame</param>
    public void SetFrame(Frame frame)
    {
      if (!ready || isDisposed)
        return;

      frame.geoJobHandle.Complete();
      frame.decompressionJobHandle.Complete();
      NativeArray<byte> pointdataGPU = pointSourceBuffer.LockBufferForWrite<byte>(0, frame.geoJob.vertexBuffer.Length); //Locking buffer is faster than GraphicsBuffer.SetData;
      frame.geoJob.vertexBuffer.CopyTo(pointdataGPU);
      pointSourceBuffer.UnlockBufferAfterWrite<byte>(frame.geoJob.vertexBuffer.Length);
      computeShaderRT.SetInt(pointCountID, frame.geoJob.vertexCount);
      int groupSize = Mathf.CeilToInt(rtPositions.width / 32f);
      computeShaderRT.Dispatch(0, groupSize, groupSize, 1);

#if UNITY_VISIONOS && INCLUDE_UNITY_POLYSPATIAL
        Unity.PolySpatial.PolySpatialObjectUtils.MarkDirty(rtPositions);
        Unity.PolySpatial.PolySpatialObjectUtils.MarkDirty(rtColors);
#endif

    }

   
    public void SetPointcloudMaterial(Material mat, bool instantiateMaterial)
    {
      SetPointcloudMaterial(mat, currentPointSize, currentPointEmission, instantiateMaterial);
    }

    public void SetPointcloudMaterial(Material mat, float pointSize, float pointEmission, bool instantiateMaterial)
    {
      StartCoroutine(SetMaterialsOverTime(mat, pointSize, pointEmission, instantiateMaterial));
    }

    /// <summary>
    /// On Polyspatial, materials need to be set slowly over time, so that RealityKit can catch up with the new materials
    /// </summary>
    /// <param name="mat"></param>
    IEnumerator SetMaterialsOverTime(Material mat, float pointSize, float pointEmission, bool instantiateMaterial)
    {
      if (meshRenderers != null)
      {
        for (int i = 0; i < meshRenderers.Count; i++)
        {
          if (meshRenderers[i] != null)
          {
            SetMaterial(meshRenderers[i], mat, i, pointSize, pointEmission, instantiateMaterial);
            yield return null;
          }
        }
      }
    }

    void SetMaterial(MeshRenderer renderer, Material mat, int meshletIndex, float pointSize, float pointEmission, bool instantiateMaterial)
    {
      if (!mat)
        mat = LoadDefaultMaterial();

      Material newMat;

      if(instantiateMaterial)
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
      for (int i = 0; i < meshRenderers.Count; i++)
      {
        meshRenderers[i].enabled = true;
      }
    }

    public void Hide()
    {
      for (int i = 0; i < meshRenderers.Count; i++)
      {
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
      Material pointcloudDefaultMaterial = new Material(Resources.Load("PolySpatial/Pointcloud_Circles_Polyspatial", typeof(Material)) as Material);

      if (pointcloudDefaultMaterial == null)
        UnityEngine.Debug.LogError("Pointcloud Quads material (Polyspatial) could not be loaded!");

      return pointcloudDefaultMaterial;
    }

    public void SetPointSize(float size)
    {
      currentPointSize = size;

      if (meshRenderers != null)
      {
        for (int i = 0; i < meshRenderers.Count; i++)
        {
          if (meshRenderers[i] != null)
            meshRenderers[i].sharedMaterial.SetFloat(pointScaleID, size);
        }
      }
    }

    public void SetPointEmission(float emission)
    {
      currentPointEmission = emission;

      if (meshRenderers != null)
      {
        for (int i = 0; i < meshRenderers.Count; i++)
        {
          if (meshRenderers[i] != null)
            meshRenderers[i].sharedMaterial.SetFloat(pointEmissionID, emission);
        }
      }
    }

    public void Dispose()
    {
      if (rtPositions != null)
        DestroyImmediate(rtPositions);
      if (rtColors != null)
        DestroyImmediate(rtColors);
      if (pointSourceBuffer != null)
        pointSourceBuffer.Dispose();

      if (meshFilters != null)
      {
        for (int i = 0; i < meshFilters.Count; i++)
        {
          if (meshFilters[i] != null)
          {
            DestroyImmediate(meshFilters[i]);
          }
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