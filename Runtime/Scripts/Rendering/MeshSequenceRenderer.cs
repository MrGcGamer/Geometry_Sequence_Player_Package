using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine.Rendering;
using UnityEngine;

namespace BuildingVolumes.Player
{
  public class MeshSequenceRenderer : MonoBehaviour, IMeshSequenceRenderer
  {
    GameObject streamedMeshObject;
    MeshFilter streamedMeshFilter;
    MeshRenderer streamedMeshRenderer;
    float currentOpacity = 1f;
    Texture2D streamedMeshTexture;

    Material meshMaterial;
    bool textured;
    bool isDisposed;

    private void Awake()
    {
      if (GetComponent<MeshRenderer>())
        GetComponent<MeshRenderer>().enabled = false;
    }

    public bool Setup(Transform parent, SequenceConfiguration config)
    {
      Dispose();
      isDisposed = false;

      streamedMeshObject = CreateStreamObject("MeshRenderer", this.transform);
      ConfigureMeshRenderer(streamedMeshObject, config, true, out streamedMeshRenderer, out streamedMeshFilter, out streamedMeshTexture);

      ChangeMaterial(meshMaterial, GeometrySequenceStream.MaterialProperties.Albedo, null, true, config.hasNormals);

      if (config.textureMode == SequenceConfiguration.TextureMode.None)
        textured = false;
      else
        textured = true;


      return true;
    }

    public void RenderFrame(Frame frame)
    {
      if (isDisposed)
        return;

      ShowGeometryData(frame, streamedMeshFilter);

      if (textured)
        ShowTextureData(frame, streamedMeshTexture);
    }

    void ShowGeometryData(Frame frame, MeshFilter meshFilter)
    {
      frame.geoJobHandle.Complete();
      frame.decompressionJobHandle.Complete();

      VertexAttributeDescriptor vp = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3);
      VertexAttributeDescriptor vn = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3);
      VertexAttributeDescriptor vt = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2);

      List<VertexAttributeDescriptor> vad = new List<VertexAttributeDescriptor>();
      vad.Add(vp);
      if (frame.sequenceConfiguration.hasNormals)
        vad.Add(vn);
      if (frame.sequenceConfiguration.hasUVs)
        vad.Add(vt);

      streamedMeshFilter.sharedMesh.SetVertexBufferParams(frame.sequenceConfiguration.maxVertexCount, vad.ToArray());
      streamedMeshFilter.sharedMesh.SetIndexBufferParams(frame.sequenceConfiguration.maxIndiceCount, IndexFormat.UInt32);

      if (frame.sequenceConfiguration.useCompression)
      {
        meshFilter.sharedMesh.SetVertexBufferData<byte>(frame.decompressionJob.vertexBuffer, 0, 0, frame.decompressionJob.vertexBuffer.Length);
      }

      else
      {
        meshFilter.sharedMesh.SetVertexBufferData<byte>(frame.vertexBufferRaw, 0, 0, frame.vertexBufferRaw.Length);
      }


      meshFilter.sharedMesh.SetIndexBufferData<byte>(frame.indiceBufferRaw, 0, 0, frame.indiceBufferRaw.Length);
      meshFilter.sharedMesh.SetSubMesh(0, new SubMeshDescriptor(0, frame.sequenceConfiguration.indiceCounts[frame.playbackIndex]), MeshUpdateFlags.DontRecalculateBounds);

      if(!frame.sequenceConfiguration.hasNormals)
       meshFilter.sharedMesh.RecalculateNormals();
    }

    void ShowTextureData(Frame frame, Texture2D texture)
    {
      frame.textureJobHandle.Complete();
      texture.LoadRawTextureData<byte>(frame.textureBufferRaw);
      texture.Apply();
    }

    public void ApplySingleTexture(Frame frame)
    {
      ShowTextureData(frame, streamedMeshRenderer.material.mainTexture as Texture2D);
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

    bool ConfigureMeshRenderer(GameObject renderObject, SequenceConfiguration config, bool hidden, out MeshRenderer renderer, out MeshFilter meshfilter, out Texture2D texture)
    {
      //Configure components
      renderer = renderObject.GetComponent<MeshRenderer>();
      meshfilter = renderObject.GetComponent<MeshFilter>();
      if (!meshfilter)
        meshfilter = renderObject.AddComponent<MeshFilter>();
      if (!meshfilter.sharedMesh)
        meshfilter.sharedMesh = new Mesh();
      if (!renderer)
        renderer = renderObject.AddComponent<MeshRenderer>();

      if (hidden)
      {
        meshfilter.hideFlags = HideFlags.DontSave;
        renderer.hideFlags = HideFlags.DontSave;
      }

      //Configure mesh
      meshfilter.sharedMesh.bounds = config.GetBounds();

      //Configure textures
      if (config.textureMode != SequenceConfiguration.TextureMode.None)
      {
        if (SequenceConfiguration.GetDeviceDependentTextureFormat() == SequenceConfiguration.TextureFormat.DDS)
          texture = new Texture2D(config.textureWidth, config.textureHeight, TextureFormat.DXT1, false);
        else if (SequenceConfiguration.GetDeviceDependentTextureFormat() == SequenceConfiguration.TextureFormat.ASTC)
          texture = new Texture2D(config.textureWidth, config.textureHeight, TextureFormat.ASTC_6x6, false);
        else
        {
          texture = new Texture2D(1, 1);
          Debug.LogError("Could not determine correct texture format for this platform!");
        }
      }

      else
        texture = new Texture2D(1, 1);

      return true;
    }

    public void ChangeMaterial(Material material, bool instantiateMaterial)
    {
      ChangeMaterial(material, GeometrySequenceStream.MaterialProperties.Albedo, null, instantiateMaterial);
    }

    public void ChangeMaterial(Material material, GeometrySequenceStream.MaterialProperties properties, List<string> customProperties, bool instantiateMaterial)
    {
      ChangeMaterial(material, properties, customProperties, instantiateMaterial, false);
    }

    public void ChangeMaterial(Material material, GeometrySequenceStream.MaterialProperties properties, List<string> customProperties, bool instantiateMaterial, bool hasNormals)
    {
      if (isDisposed)
        return;

      if (material == null)
        material = LoadDefaultMaterials(hasNormals);

      Material newMat;

      if (instantiateMaterial)
        newMat = new Material(material);
      else
        newMat = material;

      ApplyTextureToMaterial(newMat, streamedMeshTexture, properties, customProperties);

      //Re-applied on every material change, so a material swapped in mid-playback comes up at the
      //opacity already in force rather than fully opaque.
      MeshOpacity.Apply(newMat, currentOpacity, this);
      streamedMeshRenderer.sharedMaterial = newMat;
      meshMaterial = newMat;
    }

    void ApplyTextureToMaterial(Material mat, Texture tex, GeometrySequenceStream.MaterialProperties properties, List<string> customProperties)
    {
      if (mat.mainTextureScale.y > 0)
        mat.mainTextureScale = new Vector2(mat.mainTextureScale.x, mat.mainTextureScale.y * -1);

      if ((GeometrySequenceStream.MaterialProperties.Albedo & properties) == GeometrySequenceStream.MaterialProperties.Albedo)
      {
        mat.mainTexture = tex;
      }

      if ((GeometrySequenceStream.MaterialProperties.Emission & properties) == GeometrySequenceStream.MaterialProperties.Emission)
      {
        if (mat.HasProperty("_EmissionMap"))
          mat.SetTexture("_EmissionMap", tex);
      }

      if ((GeometrySequenceStream.MaterialProperties.Detail & properties) == GeometrySequenceStream.MaterialProperties.Detail)
      {
        if (mat.HasProperty("_DetailAlbedoMap"))
          mat.SetTexture("_DetailAlbedoMap", tex);
      }

      if (customProperties != null)
      {
        foreach (string prop in customProperties)
        {
          if (mat.HasProperty(prop))
            mat.SetTexture(prop, tex);
        }
      }
    }

    public void Show()
    {
      if (isDisposed)
        return;

      streamedMeshRenderer.enabled = true;
    }
    public void Hide()
    {
      if (isDisposed)
        return;

      streamedMeshRenderer.enabled = false;
    }

    /// <summary>
    /// URP Lit/Unlit, not the built-in-pipeline Standard and Unlit-Texture these used to be.
    /// </summary>
    /// <remarks>
    /// Two reasons, and the first is not about opacity at all: a built-in-pipeline shader has no
    /// DepthOnly pass, so with the renderer's Depth Priming Mode set to anything but Disabled it
    /// renders <em>nothing</em>, silently - URP primes depth from that pass and then draws opaques
    /// ZTest Equal. The second is that fading needs material-driven blend state, which URP's shaders
    /// expose and Standard's `_Mode` juggling does not.
    ///
    /// The albedo slot survives the swap without special-casing because
    /// <see cref="ApplyTextureToMaterial"/> assigns <c>mat.mainTexture</c> rather than "_MainTex" by
    /// name, and that resolves to whichever property the shader marks as its main texture - _BaseMap
    /// here. _EmissionMap and _DetailAlbedoMap exist on URP Lit under the same names.
    /// </remarks>
    Material LoadDefaultMaterials(bool hasNormals)
    {
      string path = hasNormals ? "Mesh/Mesh_Lit" : "Mesh/Mesh_Unlit";
      Material defaultMaterial = Resources.Load(path, typeof(Material)) as Material;

      if (!defaultMaterial)
        Debug.LogError("Could not load the default mesh material at Resources/" + path);

      return defaultMaterial;
    }

    public void SetOpacity(float opacity)
    {
      currentOpacity = Mathf.Clamp01(opacity);
      MeshOpacity.Apply(streamedMeshRenderer != null ? streamedMeshRenderer.sharedMaterial : null, currentOpacity, this);
    }

    public void Dispose()
    {
      if (streamedMeshTexture != null)
        DestroyImmediate(streamedMeshTexture);


      if (streamedMeshFilter != null)
        DestroyImmediate(streamedMeshFilter.sharedMesh);


      if (streamedMeshObject != null)
        DestroyImmediate(streamedMeshObject);

      isDisposed = true;
    }

    public bool IsDisposed()
    {
      return isDisposed;
    }

    public void EndEditorLife()
    {

    }

    #region DebugMeshBuffer

    public void GetVertices()
    {
      GraphicsBuffer vertexBuffer = streamedMeshFilter.sharedMesh.GetVertexBuffer(0);
      GraphicsBuffer indexBuffer = streamedMeshFilter.sharedMesh.GetIndexBuffer();

      //VertexAttributeDescriptor va1 = pcMeshFilter.sharedMesh.GetVertexAttribute(0);
      //VertexAttributeDescriptor va2 = pcMeshFilter.sharedMesh.GetVertexAttribute(1);
      //VertexAttributeDescriptor va3 = pcMeshFilter.sharedMesh.GetVertexAttribute(2);

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

  }
}