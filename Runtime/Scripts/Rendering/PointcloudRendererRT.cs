using BuildingVolumes.Player;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace BuildingVolumes.Player
{

  public class PointcloudRendererRT : MonoBehaviour, IPointCloudRenderer
  {
    ComputeShader computeShaderRT;
    RenderTexture rtPositions;
    RenderTexture rtNormals;
    RenderTexture rtColors;
    int rtResolution;

    //Triple buffering neccessary, as not all dispatches are guranteed
    //to perform in one frame
    GraphicsBuffer[] pointSourceBuffers = new GraphicsBuffer[3];
    int bufferIndex = 0;

    GameObject pcObject;
    MeshFilter pcMeshFilter;
    MeshRenderer pcMeshRenderer;

    //The quad mesh is allocated once for the whole sequence, but only the current frame's points
    //are drawn. The sequence bounds are cached rather than read back off the mesh: SetSubMesh writes
    //the same bounds it would be read from, so sourcing them there makes the value self-referential
    //and one bad write - the empty submesh the first call installs - poisons it for the whole
    //sequence. This copy comes from the sequence config and is never written from the mesh.
    Bounds sequenceBounds;
    int visiblePointCount = -1;

    Material currentPointcloudMaterial;
    float currentPointSize = 0;
    float currentPointEmission = 1;

    bool ready = true;
    bool isDisposed;

    //Compute shader property IDs
    static readonly int pointSourceBufferID = Shader.PropertyToID("_PointSourceBuffer");
    static readonly int pointSourceStrideID = Shader.PropertyToID("_SourceStride");
    static readonly int pointCountID = Shader.PropertyToID("_PointCount");
    static readonly int rtPositionsID = Shader.PropertyToID("_RTPositions");
    static readonly int rtColorsID = Shader.PropertyToID("_RTColors");
    static readonly int rtNormalsID = Shader.PropertyToID("_RTNormals");
    static readonly int rtStrideID = Shader.PropertyToID("_RTStride");
    static readonly int rtNormalsEnabledID = Shader.PropertyToID("_RTHasNormals");

    //Vertex/Fragment shader property IDs
    static readonly int rtResolutionID = Shader.PropertyToID("_RTResolution");
    static readonly int rtPositionSourceID = Shader.PropertyToID("_PositionSourceRT");
    static readonly int rtNormalSourceID = Shader.PropertyToID("_NormalSourceRT");
    static readonly int rtColorSourceID = Shader.PropertyToID("_ColorSourceRT");
    static readonly int pointScaleID = Shader.PropertyToID("_PointScale");
    static readonly int pointEmissionID = Shader.PropertyToID("_PointEmission");

    /// <summary>
    /// Prepare all the buffers for a pointcloud sequence. Only needs to bet set once per sequence
    /// </summary>
    /// <param name="maxPointCount">The maximum number of points that could appear in any frame of the sequence</param>
    /// <param name="meshFilter">The meshfilter where the point geometry data will be rendered into</param>
    /// <param name="meshRenderer">The meshrenderer used for rendering the points. Will be auto-configured</param>
    public void Setup(SequenceConfiguration configuration, Transform parent, float pointSize, float pointEmission, Material pointMaterial, bool instantiateMaterial)
    {
      Dispose();

      ready = true;
      isDisposed = false;

      if (computeShaderRT == null)
        computeShaderRT = Resources.Load("ShaderGraph/Pointcloud_Shadergraph", typeof(ComputeShader)) as ComputeShader;

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

      //Optional Render Texture for storing normals
      if (configuration.hasNormals)
      {
        rtNormals = new RenderTexture(rtResolution, rtResolution, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat);
        rtNormals.enableRandomWrite = true;
        rtNormals.filterMode = FilterMode.Point;
        rtNormals.Create();
      }

      //Create the buffer where all the raw point data will be stored
      int textureSize = rtPositions.width * rtPositions.height;
      int stride = configuration.hasNormals ? 4 * 7 : 4 * 4;

      for (int i = 0; i < pointSourceBuffers.Length; i++)
      {
        pointSourceBuffers[i] = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.LockBufferForWrite, textureSize, stride);
      }

      int kernel = configuration.hasNormals ? 1 : 0;
      computeShaderRT.SetInt(rtStrideID, rtResolution);
      computeShaderRT.SetInt(pointSourceStrideID, stride);
      computeShaderRT.SetBool(rtNormalsEnabledID, configuration.hasNormals);
      computeShaderRT.SetTexture(kernel, rtPositionsID, rtPositions);
      computeShaderRT.SetTexture(kernel, rtColorsID, rtColors);
      if (configuration.hasNormals)
        computeShaderRT.SetTexture(kernel, rtNormalsID, rtNormals);

      //Create the pointcloud mesh with n points
      pcObject = MeshCreation(configuration);

      SetPointcloudMaterial(pointMaterial, pointSize, pointEmission, instantiateMaterial, configuration.hasNormals);

    }

    /// <summary>
    /// On Polyspatial, mesh creation is a relatively expensive process
    /// We therefore need to slowly create the mesh over multiple frames
    /// and also distribute the mesh over multiple Meshfilters.
    /// Otherwise, we risk fatal crashes where the AVP needs to restart
    /// </summary>
    GameObject MeshCreation(SequenceConfiguration config)
    {
      //Setup the rendering object
      GameObject pcObject = CreateStreamObject("PointcloudRenderer", this.transform);

      pcMeshFilter = pcObject.GetComponent<MeshFilter>();
      if (pcMeshFilter == null)
        pcMeshFilter = pcObject.AddComponent<MeshFilter>();

      pcMeshRenderer = pcObject.GetComponent<MeshRenderer>();
      if (pcMeshRenderer == null)
        pcMeshRenderer = pcObject.AddComponent<MeshRenderer>();

      pcMeshFilter.hideFlags = HideFlags.HideAndDontSave;
      pcMeshRenderer.hideFlags = HideFlags.HideAndDontSave;


      //Create a mesh that we use to render the pointcloud
      //The mesh consists out of simple quads, which we define here

      int quadCount = config.maxVertexCount;

      Vector3 vertice1 = new Vector3(-0.5f, 0.5f, 0f);
      Vector3 vertice2 = new Vector3(0.5f, 0.5f, 0f);
      Vector3 vertice3 = new Vector3(0.5f, -0.5f, 0f);
      Vector3 vertice4 = new Vector3(-0.5f, -0.5f, 0f);

      Vector3 normal = new Vector3(0, 0, 1);

      Vector2 uv1 = new Vector2(0, 1);
      Vector2 uv2 = new Vector2(1, 1);
      Vector2 uv3 = new Vector2(1, 0);
      Vector2 uv4 = new Vector2(0, 0);

      Mesh mesh = new Mesh();

      Vector3[] vertices = new Vector3[quadCount * 4];
      Vector3[] normals = new Vector3[1];
      Vector2[] uvs = new Vector2[quadCount * 4];
      int[] indices = new int[quadCount * 6];
      if (config.hasNormals)
        normals = new Vector3[quadCount * 4];

      for (int i = 0; i < quadCount; i++)
      {
        vertices[i * 4 + 0] = vertice1;
        vertices[i * 4 + 1] = vertice2;
        vertices[i * 4 + 2] = vertice3;
        vertices[i * 4 + 3] = vertice4;

        uvs[i * 4 + 0] = uv1;
        uvs[i * 4 + 1] = uv2;
        uvs[i * 4 + 2] = uv3;
        uvs[i * 4 + 3] = uv4;

        if (config.hasNormals)
        {
          normals[i * 4 + 0] = normal;
          normals[i * 4 + 1] = normal;
          normals[i * 4 + 2] = normal;
          normals[i * 4 + 3] = normal;
        }

        indices[i * 6 + 0] = i * 4 + 0;
        indices[i * 6 + 1] = i * 4 + 1;
        indices[i * 6 + 2] = i * 4 + 3;
        indices[i * 6 + 3] = i * 4 + 1;
        indices[i * 6 + 4] = i * 4 + 2;
        indices[i * 6 + 5] = i * 4 + 3;
      }

      //Important, as we often deal with more than 16000 triangles
      mesh.indexFormat = IndexFormat.UInt32;

      sequenceBounds = config.GetBounds();

      mesh.vertices = vertices;
      mesh.triangles = indices;
      mesh.SetUVs(0, uvs);
      //Assigned after the vertices, which would otherwise derive bounds from the quad corners alone.
      mesh.bounds = sequenceBounds;
      if (config.hasNormals)
        mesh.normals = normals;

      pcMeshFilter.sharedMesh = mesh;

      //Nothing is drawn until the first frame arrives: the point data render textures hold stale
      //data until then, and the opaque default material has no alpha clip to hide it.
      SetVisiblePointCount(0);

      return pcObject;
    }

    /// <summary>
    /// Restricts the draw to the points the current frame actually contains.
    /// </summary>
    /// <remarks>
    /// The quad mesh is sized once for the sequence's maximum point count, so without this every
    /// frame vertex-shades and rasterizes the unused tail as well - on a sequence whose point count
    /// varies that is pure waste. It is also what makes an opaque, non-alpha-clipped point material
    /// possible: the tail used to be hidden by the compute shader writing a zero alpha, which only
    /// works if something later clips on it.
    /// </remarks>
    void SetVisiblePointCount(int pointCount)
    {
      //Checked before the property is touched: sharedMesh throws once the MeshFilter is destroyed.
      if (pcMeshFilter == null || pcMeshFilter.sharedMesh == null)
        return;

      Mesh mesh = pcMeshFilter.sharedMesh;

      //Clamped against the mesh rather than the sequence's maxVertexCount it was built from: the mesh
      //is what the indices have to stay inside of.
      pointCount = Mathf.Clamp(pointCount, 0, mesh.vertexCount / 4);

      if (pointCount == visiblePointCount)
        return;

      visiblePointCount = pointCount;

      SubMeshDescriptor subMesh = new SubMeshDescriptor(0, pointCount * 6, MeshTopology.Triangles)
      {
        firstVertex = 0,
        vertexCount = pointCount * 4,
        //Supplied explicitly because SetSubMesh replaces the submesh bounds, and the vertex positions
        //are all zero-centered quad corners, so anything else collapses the culling volume.
        bounds = sequenceBounds
      };

      //DontNotifyMeshUsers is deliberately not passed: it leaves the MeshRenderer on the bounds it
      //cached when the vertices were assigned, which for this mesh is the 1x1x0 quad-corner box.
      mesh.SetSubMesh(0, subMesh, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
      mesh.bounds = sequenceBounds;

      //Set on the renderer as well, so culling and the editor's wireframe never depend on how mesh
      //bounds propagate through SetSubMesh. localBounds overrides the mesh's bounds outright.
      if (pcMeshRenderer)
        pcMeshRenderer.localBounds = sequenceBounds;
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

      bufferIndex++;
      if (bufferIndex >= 3)
        bufferIndex = 0;

      frame.geoJobHandle.Complete();
      frame.decompressionJobHandle.Complete();
      NativeArray<byte> pointdataGPU = pointSourceBuffers[bufferIndex].LockBufferForWrite<byte>(0, frame.geoJob.vertexBuffer.Length); //Locking buffer is faster than GraphicsBuffer.SetData;
      frame.geoJob.vertexBuffer.CopyTo(pointdataGPU);
      pointSourceBuffers[bufferIndex].UnlockBufferAfterWrite<byte>(frame.geoJob.vertexBuffer.Length);
      computeShaderRT.SetInt(pointCountID, frame.geoJob.vertexCount);

      SetVisiblePointCount(frame.geoJob.vertexCount);

      //Only dispatch over the render texture rows this frame's points reach. The rows past them keep
      //stale data, which is harmless because the draw is clamped to the same point count.
      int usedRows = Mathf.CeilToInt(visiblePointCount / (float)rtResolution);
      int groupSizeX = Mathf.CeilToInt(rtPositions.width / 32f);
      int groupSizeY = Mathf.Max(1, Mathf.CeilToInt(usedRows / 32f));
      int kernel = frame.sequenceConfiguration.hasNormals ? 1 : 0;
      computeShaderRT.SetBuffer(kernel, pointSourceBufferID, pointSourceBuffers[bufferIndex]);
      computeShaderRT.Dispatch(kernel, groupSizeX, groupSizeY, 1);
    }


    public void SetPointcloudMaterial(Material mat, bool instantiateMaterial)
    {
      SetPointcloudMaterial(mat, currentPointSize, currentPointEmission, instantiateMaterial);
    }

    public void SetPointcloudMaterial(Material mat, float pointSize, float pointEmission, bool instantiateMaterial)
    {
      SetPointcloudMaterial(mat, pointSize, pointEmission, instantiateMaterial, false);
    }

    public void SetPointcloudMaterial(Material mat, float pointSize, float pointEmission, bool instantiateMaterial, bool hasNormals = false)
    {
      if (isDisposed || !pcMeshRenderer)
        return;

      if (!mat)
        mat = LoadDefaultMaterial(hasNormals);

      currentPointcloudMaterial = mat;
      currentPointSize = pointSize;
      currentPointEmission = pointEmission;

      Material newMat;

      if (instantiateMaterial)
        newMat = new Material(mat);
      else
        newMat = mat;
      newMat.SetFloat(rtResolutionID, rtResolution);
      newMat.SetTexture(rtPositionSourceID, rtPositions);
      newMat.SetTexture(rtColorSourceID, rtColors);
      if (hasNormals)
        newMat.SetTexture(rtNormalSourceID, rtNormals);

      if (newMat.HasFloat(pointScaleID))
        newMat.SetFloat(pointScaleID, pointSize);

      if (newMat.HasFloat(pointEmissionID))
        newMat.SetFloat(pointEmissionID, pointEmission);

      if (pcMeshRenderer != null)
        pcMeshRenderer.sharedMaterial = newMat;
    }

    public void Show()
    {
      if (isDisposed || !pcMeshRenderer)
        return;

      pcMeshRenderer.enabled = true;
    }

    public void Hide()
    {
      if (isDisposed || !pcMeshRenderer)
        return;

      pcMeshRenderer.enabled = false;
    }

    GameObject CreateStreamObject(string name, Transform parent)
    {
      GameObject newStreamObject = new GameObject(name);
      newStreamObject.transform.parent = this.transform;
      newStreamObject.transform.localPosition = Vector3.zero;
      newStreamObject.transform.localRotation = Quaternion.identity;
      newStreamObject.transform.localScale = Vector3.one;
      newStreamObject.hideFlags = HideFlags.DontSave;
      return newStreamObject;
    }

    public Material LoadDefaultMaterial(bool hasNormals)
    {
      if (hasNormals)
      {
        Material litMat = new Material(Resources.Load("ShaderGraph/Pointcloud_Circles_Lit_Shadergraph", typeof(Material)) as Material);

        if (litMat == null)
          UnityEngine.Debug.LogError("Pointcloud Circles Lit material could not be loaded!");

        return litMat;
      }

      //Opaque squares rather than alpha-clipped circles. A discard disables hidden surface removal on
      //Apple's tile-based GPUs, so every point occluded by the front of the capture still gets shaded;
      //dropping the clip hands that occlusion back to the hardware. This is the Quads graph with its
      //alpha clip switched off - derived from the graph that works rather than reimplementing the
      //billboard vertex logic by hand. Assign a circle material through
      //GeometrySequenceStream.customMaterial to trade the occlusion win back for round points.
      Material opaqueMat = Resources.Load("ShaderGraph/Pointcloud_Quads_Opaque_Shadergraph", typeof(Material)) as Material;

      if (opaqueMat == null)
      {
        UnityEngine.Debug.LogError("Pointcloud Quads Opaque material could not be loaded, falling back to alpha-clipped circles!");
        return new Material(Resources.Load("ShaderGraph/Pointcloud_Circles_Shadergraph", typeof(Material)) as Material);
      }

      return new Material(opaqueMat);
    }

    public void SetPointSize(float size)
    {
      if (isDisposed || !pcMeshRenderer)
        return;

      if (pcMeshRenderer.sharedMaterial.HasFloat(pointScaleID))
        pcMeshRenderer.sharedMaterial.SetFloat(pointScaleID, size);

      currentPointSize = size;
    }

    public void SetPointEmission(float emission)
    {
      if (isDisposed || !pcMeshRenderer)
        return;

      if (pcMeshRenderer.sharedMaterial.HasFloat(pointEmissionID))
        pcMeshRenderer.sharedMaterial.SetFloat(pointEmissionID, emission);

      currentPointEmission = emission;
    }

    public void Dispose()
    {
      if (rtPositions != null)
        DestroyImmediate(rtPositions);
      if (rtColors != null)
        DestroyImmediate(rtColors);
      if (rtNormals != null)
        DestroyImmediate(rtNormals);
      for (int i = 0; i < pointSourceBuffers.Length; i++)
      {
        if (pointSourceBuffers[i] != null)
          pointSourceBuffers[i].Dispose();
      }

      //Destroying pcObject only takes the MeshFilter with it, not the mesh it points at. The mesh is
      //built per sequence and owned solely by this renderer, and at maxVertexCount * 4 vertices it is
      //far too big to leave to the GC - in the editor ThumbnailLoadHelper re-runs Setup on every
      //domain reload, so one is stranded per recompile.
      if (pcMeshFilter != null && pcMeshFilter.sharedMesh != null)
        DestroyImmediate(pcMeshFilter.sharedMesh);

      if (pcObject != null)
        DestroyImmediate(pcObject);

      visiblePointCount = -1;

      isDisposed = true;
    }

    public bool IsDisposed()
    {
      return isDisposed;
    }

  }

}