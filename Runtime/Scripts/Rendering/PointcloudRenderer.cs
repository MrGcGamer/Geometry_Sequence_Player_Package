using System;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BuildingVolumes.Player
{
  /// <summary>
  /// The legacy render path: a compute shader writes camera-facing quads straight into the vertex
  /// and index buffers of a mesh.
  /// </summary>
  public class PointcloudRenderer : PointcloudRendererBase
  {
    private ComputeShader computeShader;

    //Triple buffered graphics buffer
    GraphicsBuffer[] pointSourceBuffers = new GraphicsBuffer[3];
    int pointSourceCount;

    GraphicsBuffer vertexBuffer;
    GraphicsBuffer indexBuffer;
    GraphicsBuffer rotateToCameraMat;

    MeshFilter pcMeshFilter;
    MeshRenderer pcMeshRenderer;
    Mesh pcMesh;

    Camera cam;

    int maxPointCount;
    bool isDataSet;
    int bufferIndex;
    int lastBufferUpdateFrame = -1;

    static readonly int vertexBufferID = Shader.PropertyToID("_VertexBuffer");
    static readonly int indexBufferID = Shader.PropertyToID("_IndexBuffer");
    static readonly int rotateToCameraID = Shader.PropertyToID("_RotateToCamera");
    static readonly int bufferStrideID = Shader.PropertyToID("_BufferStride");

    protected override string StreamObjectName => "PointcloudRenderer";

    /// <summary>
    /// Prepares all buffers used for pointcloud rendering. All buffers are allocated with the max
    /// possible size that can appear in the sequence to avoid re-allocations.
    /// </summary>
    protected override void OnSetup(SequenceConfiguration config)
    {
      pcMesh = CreateStreamMesh(out pcMeshFilter, out pcMeshRenderer);

      if (computeShader == null)
        computeShader = (ComputeShader)Instantiate(Resources.Load("Legacy/Pointcloud", typeof(ComputeShader)));

      int byteStride = SourceByteStride;

      rotateToCameraMat = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4 * 4 * 4);

      for (int i = 0; i < pointSourceBuffers.Length; i++)
        pointSourceBuffers[i] = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.LockBufferForWrite, config.maxVertexCount, byteStride);

      pcMesh.indexBufferTarget |= GraphicsBuffer.Target.Raw;
      pcMesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;

      VertexAttributeDescriptor vp = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3);
      VertexAttributeDescriptor vn = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3);
      VertexAttributeDescriptor vc = new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4);
      VertexAttributeDescriptor vt = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2);

      if (config.hasNormals)
        pcMesh.SetVertexBufferParams(config.maxVertexCount * 4, vp, vn, vc, vt);
      else
        pcMesh.SetVertexBufferParams(config.maxVertexCount * 4, vp, vc, vt);

      pcMesh.SetIndexBufferParams(config.maxVertexCount * 6, IndexFormat.UInt32);
      pcMesh.SetSubMesh(0, new SubMeshDescriptor(0, config.maxVertexCount * 6), MeshUpdateFlags.DontRecalculateBounds);

      vertexBuffer = pcMesh.GetVertexBuffer(0);
      indexBuffer = pcMesh.GetIndexBuffer();

      computeShader.SetInt(bufferStrideID, byteStride);

      int kernel = config.hasNormals ? 1 : 0;
      computeShader.SetInt(pointSourceCount, 0);
      computeShader.SetBuffer(kernel, vertexBufferID, vertexBuffer);
      computeShader.SetBuffer(kernel, indexBufferID, indexBuffer);
      computeShader.SetBuffer(kernel, rotateToCameraID, rotateToCameraMat);
      computeShader.SetBuffer(kernel, pointSourceBufferID, pointSourceBuffers[0]);
      //Run a shader dispatch that creates only invisible quads to clean the buffers
      int groupSize = Mathf.CeilToInt(config.maxVertexCount / 128f);
      computeShader.Dispatch(kernel, groupSize, 1, 1);

      //The point size lives in the compute shader rather than the material, so the value handed to
      //Setup has to be pushed there separately from the material binding the base does.
      SetPointSize(currentPointSize);

      RegisterRenderer(pcMeshRenderer);

#if UNITY_EDITOR
      if (!Application.isPlaying)
        StartEditorLife();
#endif
    }

    /// <summary>
    /// Set the pointcloud data to be rendered. Does only need to be set once per Pointcloud
    /// </summary>
    public override void SetFrame(Frame frame)
    {
      if (!ready || isDisposed)
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
      NativeArray<byte> pointdataGPU = pointBuffer.LockBufferForWrite<byte>(0, pointBuffer.count * SourceByteStride); //Locking buffer is faster than GraphicsBuffer.SetData;
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
      if (!isDataSet || !ready || isDisposed)
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
    public override void SetPointSize(float size)
    {
      if (isDisposed || computeShader == null)
        return;

      currentPointSize = size;
      //Halved: the compute shader grows the quad from the point outwards, so the raw value would
      //produce a diameter twice the one that was asked for.
      computeShader.SetFloat(pointScaleID, size / 2);

#if UNITY_EDITOR
      if (!Application.isPlaying)
      {
        Render();
        SceneView.RepaintAll();
      }
#endif
    }

    /// <summary>
    /// The quads are sized in the compute shader, so nothing about the size reaches the material.
    /// This is already done by <see cref="SetPointSize"/>
    /// </summary>
    protected override void ApplyPointSize(int rendererIndex) { }

    protected override Material LoadDefaultMaterial()
    {
      Material mat;

      if (configuration.hasNormals)
        mat = Resources.Load("Legacy/Pointcloud_Circles_Legacy_Lit", typeof(Material)) as Material;
      else
        mat = Resources.Load("Legacy/Pointcloud_Circles_Legacy", typeof(Material)) as Material;

      if (!mat)
        Debug.LogError("Could not load default pointcloud material at Resources/Legacy/Pointcloud_Circles_Legacy");

      return mat;
    }

    protected override void ReleaseResources()
    {
      if (vertexBuffer != null)
        vertexBuffer.Release();
      if (indexBuffer != null)
        indexBuffer.Release();
      if (rotateToCameraMat != null)
        rotateToCameraMat.Release();

      vertexBuffer = null;
      indexBuffer = null;
      rotateToCameraMat = null;

      for (int i = 0; i < pointSourceBuffers.Length; i++)
      {
        if (pointSourceBuffers[i] != null)
          pointSourceBuffers[i].Release();

        pointSourceBuffers[i] = null;
      }

      if (computeShader != null)
        DestroyImmediate(computeShader);

      computeShader = null;
      isDataSet = false;
    }

    #region DebugMeshBuffer

    public void GetVertices()
    {
      GraphicsBuffer vertexBuffer = pcMeshFilter.sharedMesh.GetVertexBuffer(0);
      GraphicsBuffer indexBuffer = pcMeshFilter.sharedMesh.GetIndexBuffer();

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
