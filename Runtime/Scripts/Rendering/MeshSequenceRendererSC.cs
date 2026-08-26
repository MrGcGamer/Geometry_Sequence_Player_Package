using System.Collections.Generic;
using UnityEngine.Rendering;
using UnityEngine;

namespace BuildingVolumes.Player
{
  public class MeshSequenceRendererSC : MonoBehaviour, IMeshSequenceRenderer
  {
    GameObject streamedMeshParent;
    List<GameObject> streamedMeshObjects = new List<GameObject>();
    List<MeshFilter> streamedMeshFilters = new List<MeshFilter>();
    List<MeshRenderer> streamedMeshRenderers = new List<MeshRenderer>();
    List<Texture2D> streamedMeshTextures = new List<Texture2D>();
    float currentOpacity = 1f;

    Material meshMaterial;
    bool textured;
    int swapChainSize;
    int swapChainIndex;

    bool isDisposed;

    private void Awake()
    {
      if (GetComponent<MeshRenderer>())
        GetComponent<MeshRenderer>().enabled = false;
    }

    public bool Setup(Transform parent, SequenceConfiguration config)
    {
      return Setup(parent, config, 3);
    }

    public bool Setup(Transform parent, SequenceConfiguration config, int swapChainSize = 3)
    {
      Dispose();
      isDisposed = false;

      this.swapChainSize = swapChainSize;
      this.swapChainIndex = 0;

      string name = "MeshSequence";
#if UNITY_EDITOR
      if (!Application.isPlaying)
        name = "Thumbnail";
#endif

      streamedMeshParent = CreateStreamObject(name, parent);

      for (int i = 0; i < swapChainSize; i++)
      {
        GameObject meshObject = CreateStreamObject("MeshRenderer_" + i, streamedMeshParent.transform);
        MeshRenderer meshRenderer;
        MeshFilter meshFilter;
        Texture2D meshTexture;

        ConfigureMeshRenderer(meshObject, config, true, out meshRenderer, out meshFilter, out meshTexture);

        streamedMeshObjects.Add(meshObject);
        streamedMeshRenderers.Add(meshRenderer);
        streamedMeshFilters.Add(meshFilter);
        streamedMeshTextures.Add(meshTexture);
      }

      ChangeMaterial(meshMaterial, true);

      if (config.textureMode == SequenceConfiguration.TextureMode.None)
        textured = false;
      else
        textured = true;


      return true;
    }

    public void RenderFrame(Frame frame)
    {
      //Update the next swapchain buffer render object with data
      swapChainIndex++;

      if (swapChainIndex >= swapChainSize)
        swapChainIndex = 0;

      ShowGeometryData(frame, streamedMeshFilters[swapChainIndex]);

      if (textured)
        ShowTextureData(frame, streamedMeshTextures[swapChainIndex]);

      streamedMeshObjects[swapChainIndex].name = "Buffered Frame: " + swapChainIndex;

      //Show the swapchain buffer object which had the longest time since it's
      //last update, which gives us the highest chance of it being already copied to 
      //the RealityKit renderer on Polyspatial

      int showIndex = swapChainIndex;
      if (showIndex >= swapChainSize)
        showIndex = 0;

      for (int i = 0; i < swapChainSize; i++)
      {
        if (i == showIndex)
          streamedMeshRenderers[i].gameObject.transform.localScale = Vector3.one;
        else
          streamedMeshRenderers[i].gameObject.transform.localScale = Vector3.zero;
#if UNITY_EDITOR
        if (!Application.isPlaying)
          streamedMeshRenderers[i].gameObject.transform.localScale = Vector3.one;
#endif
      }
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

      meshFilter.sharedMesh.SetVertexBufferParams(frame.sequenceConfiguration.maxVertexCount, vad.ToArray());
      meshFilter.sharedMesh.SetIndexBufferParams(frame.sequenceConfiguration.maxIndiceCount, IndexFormat.UInt32);

      meshFilter.sharedMesh.SetVertexBufferData<byte>(frame.vertexBufferRaw, 0, 0, frame.vertexBufferRaw.Length);
      meshFilter.sharedMesh.SetIndexBufferData<byte>(frame.indiceBufferRaw, 0, 0, frame.indiceBufferRaw.Length);
      meshFilter.sharedMesh.SetSubMesh(0, new SubMeshDescriptor(0, frame.sequenceConfiguration.indiceCounts[frame.playbackIndex]), MeshUpdateFlags.DontRecalculateBounds);

      if (!frame.sequenceConfiguration.hasNormals)
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
      for (int i = 0; i < streamedMeshRenderers.Count; i++)
      {
        ShowTextureData(frame, streamedMeshRenderers[i].material.mainTexture as Texture2D);
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
      for (int i = 0; i < streamedMeshRenderers.Count; i++)
      {
        if (material == null)
          material = LoadDefaultMaterials();

        Material newMat;

        if (instantiateMaterial)
          newMat = new Material(material);
        else
          newMat = material;

        ApplyTextureToMaterial(newMat, streamedMeshTextures[i], properties, customProperties);
        MeshOpacity.Apply(newMat, currentOpacity, this);
        streamedMeshRenderers[i].sharedMaterial = newMat;
      }
    }

    void ApplyTextureToMaterial(Material mat, Texture tex, GeometrySequenceStream.MaterialProperties properties, List<string> customProperties)
    {
      if ((GeometrySequenceStream.MaterialProperties.Albedo & properties) == GeometrySequenceStream.MaterialProperties.Albedo)
      {
        if (mat.HasProperty("_MainTex"))
          mat.SetTexture("_MainTex", tex);
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


      Vector2 scale = mat.GetTextureScale("_MainTex");
      mat.SetTextureScale("_MainTex", new Vector2(scale.x, scale.y * -1));
    }

    public void Show()
    {
      for (int i = 0; i < streamedMeshRenderers.Count; i++)
      {
        streamedMeshRenderers[i].enabled = true;
      }
    }
    public void Hide()
    {
      for (int i = 0; i < streamedMeshRenderers.Count; i++)
      {
        streamedMeshRenderers[i].enabled = false;
      }
    }

    Material LoadDefaultMaterials()
    {
      Material newMat = Resources.Load("ShaderGraph/Unlit_Mesh", typeof(Material)) as Material;
      return newMat;
    }

    /// <remarks>
    /// The default here is the Unlit_Mesh graph, whose only properties are _MainTex and _UV_Tiling:
    /// its Alpha output is a constant, so there is nothing for an opacity to scale. Giving this path
    /// opacity means adding an alpha input inside the graph, which is node editing rather than the
    /// one settings flag the pointcloud graphs needed. Until then MeshOpacity warns, naming the
    /// shader, instead of leaving the slider silently inert.
    /// </remarks>
    public void SetOpacity(float opacity)
    {
      currentOpacity = Mathf.Clamp01(opacity);

      for (int i = 0; i < streamedMeshRenderers.Count; i++)
        if (streamedMeshRenderers[i] != null)
          MeshOpacity.Apply(streamedMeshRenderers[i].sharedMaterial, currentOpacity, this);
    }

    public void Dispose()
    {
      if (streamedMeshTextures != null)
      {
        for (int i = 0; i < streamedMeshTextures.Count; i++)
        {
          if (streamedMeshTextures[i] != null)
            DestroyImmediate(streamedMeshTextures[i]);
        }
      }

      if (streamedMeshFilters != null)
      {
        for (int i = 0; i < streamedMeshFilters.Count; i++)
        {
          if (streamedMeshFilters[i] != null)
            DestroyImmediate(streamedMeshFilters[i].sharedMesh);
        }
      }

      if (streamedMeshObjects != null)
      {
        for (int i = 0; i < streamedMeshObjects.Count; i++)
        {
          if (streamedMeshObjects[i] != null)
            DestroyImmediate(streamedMeshObjects[i]);
        }
      }


      streamedMeshObjects?.Clear();
      streamedMeshFilters?.Clear();
      streamedMeshRenderers?.Clear();

      if (streamedMeshParent != null)
        DestroyImmediate(streamedMeshParent);

      isDisposed = true;
    }

    public bool IsDisposed()
    {
      return isDisposed;
    }

    public void EndEditorLife()
    {

    }
  }
}