using System;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;


namespace BuildingVolumes.Player
{
  public class PointcloudRenderer : MonoBehaviour, IPointCloudRenderer
  {
    SequenceConfiguration configuration;
    private ComputeShader computeShader;
    private float pointScale = 0.005f;
    private float pointEmission = 1f;

    //Triple buffered graphics buffer
    GraphicsBuffer[] pointSourceBuffers = new GraphicsBuffer[3];
    int pointSourceCount;

    int sourceBufferByteStride = 4 * 4;
    int sourceBufferByteStrideNormals = 4 * 7;

    GraphicsBuffer vertexBuffer;
    GraphicsBuffer indexBuffer;
    GraphicsBuffer rotateToCameraMat;

    GameObject pcObject;
    MeshFilter pcMeshFilter;
    MeshRenderer pcMeshRenderer;
    Mesh pcMesh;

    Material pointcloudMaterial;

    Camera cam;

    int maxPointCount;
    bool isDataSet;
    bool buffersInitialized;
    int bufferIndex;
    int lastBufferUpdateFrame = -1;
    bool isDisposed;

    static readonly int vertexBufferID = Shader.PropertyToID("_VertexBuffer");
    static readonly int indexBufferID = Shader.PropertyToID("_IndexBuffer");
    static readonly int rotateToCameraID = Shader.PropertyToID("_RotateToCamera");
    static readonly int pointScaleID = Shader.PropertyToID("_PointScale");
    static readonly int bufferStrideID = Shader.PropertyToID("_BufferStride");
    static readonly int pointCountID = Shader.PropertyToID("_PointCount");
    static readonly int pointSourceBufferID = Shader.PropertyToID("_PointSourceBuffer");


    /// <summary>
    /// Prepares all buffers used for pointcloud rendering. This needs to be executed once per sequence
    /// All buffers will be allocated with the max. possible size that can appear in the sequence to avoid
    /// re-allocations
    /// </summary>
    /// <param name="maxPointCount">The max. numbers of point that will be shown in any frame of the sequence</param>
    /// <param name="meshfilter">The mesh filter which will contains the final mesh of the pointclouds</param>
    /// <param name="pointSize">The diameter of the points in Unity units</param>
    /// <returns></returns>
    public void Setup(SequenceConfiguration configuration, Transform parent, float pointSize, float emission, Material mat, bool instantiateMaterial)
    {
      Dispose();
      isDisposed = false;


      this.configuration = configuration;

      pcObject = CreateStreamObject("PointcloudRenderer", parent);

      pcMeshFilter = pcObject.GetComponent<MeshFilter>();
      if (pcMeshFilter == null)
        pcMeshFilter = pcObject.AddComponent<MeshFilter>();

      pcMeshRenderer = pcObject.GetComponent<MeshRenderer>();
      if (pcMeshRenderer == null)
        pcMeshRenderer = pcObject.AddComponent<MeshRenderer>();

      if (pcMeshFilter.sharedMesh == null)
        pcMeshFilter.sharedMesh = new Mesh();

      pcMeshFilter.hideFlags = HideFlags.DontSave;
      pcMeshRenderer.hideFlags = HideFlags.DontSave;

      pcMesh = pcMeshFilter.sharedMesh;

      pcMesh.bounds = configuration.GetBounds();

      //pcMesh.bounds = configuration.GetBounds();

      if (computeShader == null)
        computeShader = (ComputeShader)Instantiate(Resources.Load("Legacy/Pointcloud", typeof(ComputeShader)));

      int byteStride = configuration.hasNormals ? sourceBufferByteStrideNormals : sourceBufferByteStride;

      rotateToCameraMat = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4 * 4 * 4);

      for (int i = 0; i < pointSourceBuffers.Length; i++)
        pointSourceBuffers[i] = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.LockBufferForWrite, configuration.maxVertexCount, byteStride);

      pcMesh.indexBufferTarget |= GraphicsBuffer.Target.Raw;
      pcMesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;

      VertexAttributeDescriptor vp = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3);
      VertexAttributeDescriptor vn = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3);
      VertexAttributeDescriptor vc = new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4);
      VertexAttributeDescriptor vt = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2);

      if (configuration.hasNormals)
        pcMesh.SetVertexBufferParams(configuration.maxVertexCount * 4, vp, vn, vc, vt);
      else
        pcMesh.SetVertexBufferParams(configuration.maxVertexCount * 4, vp, vc, vt);

      pcMesh.SetIndexBufferParams(configuration.maxVertexCount * 6, IndexFormat.UInt32);
      pcMesh.SetSubMesh(0, new SubMeshDescriptor(0, configuration.maxVertexCount * 6), MeshUpdateFlags.DontRecalculateBounds);

      vertexBuffer = pcMesh.GetVertexBuffer(0);
      indexBuffer = pcMesh.GetIndexBuffer();

      computeShader.SetInt(bufferStrideID, byteStride);

      int kernel = configuration.hasNormals ? 1 : 0;
      computeShader.SetInt(pointSourceCount, 0);
      computeShader.SetBuffer(kernel, vertexBufferID, vertexBuffer);
      computeShader.SetBuffer(kernel, indexBufferID, indexBuffer);
      computeShader.SetBuffer(kernel, rotateToCameraID, rotateToCameraMat);
      computeShader.SetBuffer(kernel, pointSourceBufferID, pointSourceBuffers[0]);
      //Run a shader dispatch that creates only invisible quads to clean the buffers
      int groupSize = Mathf.CeilToInt(configuration.maxVertexCount / 128f);
      computeShader.Dispatch(configuration.hasNormals ? 1 : 0, groupSize, 1, 1);

      pointcloudMaterial = mat;
      if (!pointcloudMaterial)
        pointcloudMaterial = LoadDefaultMaterial(configuration.hasNormals);
      SetPointcloudMaterial(mat, pointSize, emission, instantiateMaterial);

      buffersInitialized = true;

#if UNITY_EDITOR
      if (!Application.isPlaying)
        StartEditorLife();
#endif
    }

    /// <summary>
    /// Set the pointcloud data to be rendered. Does only need to be set once per Pointcloud
    /// </summary>
    /// <param name="pointSource">The point positions and colors as a native array</param>
    /// <param name="sourceCount">The amount of points contained in the array</param>
    public void SetFrame(Frame frame)
    {
      if (!buffersInitialized || isDisposed)
        return;
      if (lastBufferUpdateFrame == Time.frameCount)
        return;

      frame.geoJobHandle.Complete();
      if (configuration.useCompression)
        frame.decompressionJobHandle.Complete();

      bufferIndex++;
      if (bufferIndex >= 3)
        bufferIndex = 0;

      pointSourceCount = frame.geoJob.vertexCount;
      UpdatePointBuffer(frame, pointSourceBuffers[bufferIndex]);

      lastBufferUpdateFrame = Time.frameCount;
    }

    void UpdatePointBuffer(Frame frame, GraphicsBuffer pointBuffer)
    {
      maxPointCount = pointBuffer.count;
      pointSourceCount = frame.geoJob.vertexCount;
      int byteStride = configuration.hasNormals ? sourceBufferByteStrideNormals : sourceBufferByteStride;
      NativeArray<byte> pointdataGPU = pointBuffer.LockBufferForWrite<byte>(0, pointBuffer.count * byteStride); //Locking buffer is faster than GraphicsBuffer.SetData;
      if (configuration.useCompression)
      {
        frame.decompressionJob.vertexBuffer.CopyTo(pointdataGPU);
        pointBuffer.UnlockBufferAfterWrite<byte>(frame.decompressionJob.vertexBuffer.Length);
      }
      else
      {
        frame.geoJob.vertexBuffer.CopyTo(pointdataGPU);
        pointBuffer.UnlockBufferAfterWrite<byte>(frame.geoJob.vertexBuffer.Length);
      }
      isDataSet = true;
    }

    private void Update()
    {
      Render();
    }

    /// <summary>
    /// Renders the currently set pointcloud to the screen. This process is sensitive to the camera used for rendering,
    /// as the points always need to be oriented towards the camera.
    /// </summary>
    void Render()
    {
      if (!isDataSet || !buffersInitialized || isDisposed)
        return;

#if UNITY_EDITOR
      cam = Camera.current;
#endif
      if (cam == null)
        cam = Camera.main;

      if (cam == null)
      {
        Debug.LogError("Could not find main camera. Please tag one camera as Main Camera for the Pointcloud renderer to work");
        return;
      }

      //Rotation that lets the points face the camera
      Quaternion fromObjectToCamera = Quaternion.Inverse(transform.rotation) * (Quaternion.LookRotation(cam.transform.forward, cam.transform.up));
      Matrix4x4 rotateToCamMat = Matrix4x4.Rotate(fromObjectToCamera);
      rotateToCameraMat.SetData(new Matrix4x4[] { rotateToCamMat });
      int groupSize = Mathf.CeilToInt(maxPointCount / 128f);
      int kernel = configuration.hasNormals ? 1 : 0;
      computeShader.SetBuffer(kernel, pointSourceBufferID, pointSourceBuffers[bufferIndex]);
      computeShader.SetInt(pointCountID, pointSourceCount);
      computeShader.Dispatch(kernel, groupSize, 1, 1);
    }

    /// <summary>
    /// Set the point diameter in Unity units. 
    /// </summary>
    /// <param name="size"></param>
    public void SetPointSize(float size)
    {
      if (isDisposed)
        return;

      pointScale = size / 2; //Divide by two, otherwise the diameter will be twice as large as expected
      computeShader.SetFloat(pointScaleID, pointScale);

#if UNITY_EDITOR
      if (!Application.isPlaying)
      {
        Render();
        SceneView.RepaintAll();
      }
#endif
    }

    public void SetPointEmission(float emission)
    {
      if (isDisposed || !pcMeshRenderer)
        return;

      if (pcMeshRenderer.sharedMaterial.HasProperty("_Emission"))
        pcMeshRenderer.sharedMaterial.SetFloat("_Emission", emission);

      pointEmission = emission;
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

    public void SetPointcloudMaterial(Material mat, bool instantiateMaterial)
    {
      if (pcMeshRenderer)
      {
        if (mat)
          if (instantiateMaterial)
            pcMeshRenderer.material = new Material(mat);
          else
            pcMeshRenderer.material = mat;
        else
          pcMeshRenderer.material = LoadDefaultMaterial(configuration.hasNormals);
      }
    }

    public void SetPointcloudMaterial(Material mat, float pointSize, float pointEmission, bool instantiateMaterial)
    {
      if (isDisposed)
        return;

      SetPointcloudMaterial(mat, instantiateMaterial);
      SetPointSize(pointSize);
      SetPointEmission(pointEmission);
    }

    public void Show()
    {
      if (!isDisposed)
        pcMeshRenderer.enabled = true;
    }

    public void Hide()
    {
      if (!isDisposed)
        pcMeshRenderer.enabled = false;
    }

    public Material LoadDefaultMaterial(bool normalSupported = false)
    {
      Material mat;

      if (normalSupported)
        mat = Resources.Load("Legacy/Pointcloud_Circles_Legacy_Lit", typeof(Material)) as Material;
      else
        mat = Resources.Load("Legacy/Pointcloud_Circles_Legacy", typeof(Material)) as Material;

      if (!mat)
        Debug.LogError("Could not load default pointcloud material at Resources/Legacy/Pointcloud_Circles_Legacy");

      return mat;

    }

    public void Dispose()
    {
      if (vertexBuffer != null)
        vertexBuffer.Release();
      if (indexBuffer != null)
        indexBuffer.Release();
      if (rotateToCameraMat != null)
        rotateToCameraMat.Release();

      for (int i = 0; i < pointSourceBuffers.Length; i++)
        if (pointSourceBuffers[i] != null)
          pointSourceBuffers[i].Release();

      if (pcMeshFilter != null)
        if (pcMeshFilter.sharedMesh == null)
          DestroyImmediate(pcMeshFilter.sharedMesh);
      if (pcObject != null)
        DestroyImmediate(pcObject);

      buffersInitialized = false;
      isDataSet = false;

      if (computeShader != null)
        DestroyImmediate(computeShader);

      isDisposed = true;
    }

    public bool IsDisposed()
    {
      return isDisposed;
    }

    #region DebugMeshBuffer


    public void GetVertices()
    {
      GraphicsBuffer vertexBuffer = pcMeshFilter.sharedMesh.GetVertexBuffer(0);
      GraphicsBuffer indexBuffer = pcMeshFilter.sharedMesh.GetIndexBuffer();

      //VertexAttributeDescriptor va1 = pcMeshFilter.sharedMesh.GetVertexAttribute(0);
      //VertexAttributeDescriptor va2 = pcMeshFilter.sharedMesh.GetVertexAttribute(1);
      //VertexAttributeDescriptor va3 = pcMeshFilter.sharedMesh.GetVertexAttribute(2);
      //VertexAttributeDescriptor va4 = pcMeshFilter.sharedMesh.GetVertexAttribute(3);

      byte[] vertexBufferData = new byte[vertexBuffer.count * vertexBuffer.stride];
      vertexBuffer.GetData(vertexBufferData);

      byte[] indexBufferData = new byte[indexBuffer.count * indexBuffer.stride];
      indexBuffer.GetData(indexBufferData);

      uint[] indexBufferUint = new uint[indexBuffer.count];
      for (int i = 0; i < indexBuffer.count; i++)
      {
        indexBufferUint[i] = BitConverter.ToUInt32(indexBufferData, i * 4);
      }

      Vector3[] vPositions = new Vector3[vertexBuffer.count];
      Vector3[] vNormals = new Vector3[vertexBuffer.count];
      uint[] vColors = new uint[vertexBuffer.count];
      Vector2[] vUVs = new Vector2[vertexBuffer.count];

      int byteAdress = 0;

      for (int i = 0; i < vertexBuffer.count; i++)
      {
        float xPos = BitConverter.ToSingle(vertexBufferData, byteAdress + 0);
        float yPos = BitConverter.ToSingle(vertexBufferData, byteAdress + 4);
        float zPos = BitConverter.ToSingle(vertexBufferData, byteAdress + 8);
        vPositions[i] = new Vector3(xPos, yPos, zPos);
        byteAdress += 12;

        float xNor = BitConverter.ToSingle(vertexBufferData, byteAdress + 0);
        float yNor = BitConverter.ToSingle(vertexBufferData, byteAdress + 4);
        float zNor = BitConverter.ToSingle(vertexBufferData, byteAdress + 8);

        vNormals[i] = new Vector3(xNor, yNor, zNor);
        byteAdress += 12;

        vColors[i] = BitConverter.ToUInt32(vertexBufferData, byteAdress);
        byteAdress += 4;

        float texCoorU = BitConverter.ToSingle(vertexBufferData, byteAdress + 0);
        float texCoorV = BitConverter.ToSingle(vertexBufferData, byteAdress + 4);
        vUVs[i] = new Vector2(texCoorU, texCoorV);
        byteAdress += 8;
      }




      //fTileMeshFilter.sharedMesh.SetVertexBufferData<byte>(vertexBufferData, 0, 0, vertexBufferData.Length);
      //fTileMeshFilter.sharedMesh.SetIndexBufferData<byte>(indexBufferData, 0, 0, indexBufferData.Length);
      //fTileMeshFilter.sharedMesh.UploadMeshData(false);

      Debug.Log("Gottem");
    }


    #endregion


    #region RenderInEditor

#if UNITY_EDITOR
    public void StartEditorLife()
    {
      SceneView.beforeSceneGui += RenderInEditor;
    }

    public void RenderInEditor(SceneView view)
    {
      if (this == null)
        EndEditorLife();
      else
        Render();
    }

    public void EndEditorLife()
    {
      SceneView.beforeSceneGui -= RenderInEditor;
      Dispose();
    }


#endif

    #endregion
  }
}


