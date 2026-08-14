using UnityEngine;
using UnityEngine.Rendering;

namespace BuildingVolumes.Player
{
  /// <summary>
  /// Shared base for renderers that draw a pointcloud sequence as MeshTopology.Points
  /// (one vertex per point, no billboard quad, no compute pass). Handles stream object/mesh setup,
  /// material, point size/emission, show/hide, and teardown.
  /// Subclasses supply SetFrame and, optionally, ConfigureMesh for their data path.
  /// </summary>
  public abstract class PointcloudPointsRendererBase : MonoBehaviour, IPointCloudRenderer
  {
    protected SequenceConfiguration configuration;

    protected GameObject pcObject;
    protected MeshFilter pcMeshFilter;
    protected MeshRenderer pcMeshRenderer;
    protected Mesh pcMesh;

    protected Material pointcloudMaterial;

    protected bool buffersInitialized;
    protected bool isDisposed;

    //Shared default material/shader for every Points-topology path. Lives under a render-path-neutral
    //Resources/Points folder (not Draco) because the shader is codec-agnostic - Draco just happens to
    //have wired it first.
    const string defaultMaterialResourcePath = "Points/Pointcloud_Points";

    static readonly int pointSizeID = Shader.PropertyToID("_PointSize");
    static readonly int emissionID = Shader.PropertyToID("_Emission");

    protected const MeshUpdateFlags meshUpdateFlags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;

    /// <summary>Name given to the child GameObject that carries the MeshFilter/MeshRenderer.</summary>
    protected abstract string StreamObjectName { get; }

    public void Setup(SequenceConfiguration configuration, Transform parent, float pointSize, float emission, Material mat, bool instantiateMaterial)
    {
      Dispose();
      isDisposed = false;

      this.configuration = configuration;

      pcObject = CreateStreamObject(StreamObjectName, parent);

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

      ConfigureMesh(configuration);

      SetPointcloudMaterial(mat, pointSize, emission, instantiateMaterial);

      buffersInitialized = true;
    }

    /// <summary>
    /// Per-source mesh layout. The raw-buffer path declares the interleaved vertex layout and a
    /// sequential index buffer here; the Draco path lets Mesh.ApplyAndDisposeWritableMeshData define
    /// the layout per frame, so it does nothing.
    /// </summary>
    protected virtual void ConfigureMesh(SequenceConfiguration config) { }

    public abstract void SetFrame(Frame frame);

    public void SetPointSize(float size)
    {
      if (isDisposed || pointcloudMaterial == null)
        return;

      //_PointSize is a world-space point diameter; the shader folds in the object scale so this
      //matches the quad-based render paths (Legacy/Shadergraph) at any object scale.
      pointcloudMaterial.SetFloat(pointSizeID, size);
    }

    public void SetPointEmission(float emission)
    {
      if (isDisposed || pointcloudMaterial == null)
        return;

      if (pointcloudMaterial.HasProperty(emissionID))
        pointcloudMaterial.SetFloat(emissionID, emission);
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
      if (!pcMeshRenderer)
        return;

      if (mat)
      {
        if (instantiateMaterial)
          pointcloudMaterial = new Material(mat);
        else
          pointcloudMaterial = mat;
      }
      else
      {
        pointcloudMaterial = LoadDefaultMaterial();
      }

      pcMeshRenderer.material = pointcloudMaterial;
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
      if (!isDisposed && pcMeshRenderer)
        pcMeshRenderer.enabled = true;
    }

    public void Hide()
    {
      if (!isDisposed && pcMeshRenderer)
        pcMeshRenderer.enabled = false;
    }

    protected Material LoadDefaultMaterial()
    {
      Material shared = Resources.Load(defaultMaterialResourcePath, typeof(Material)) as Material;

      if (!shared)
      {
        Debug.LogError("Could not load default pointcloud material at Resources/" + defaultMaterialResourcePath);
        return null;
      }

      //Instantiate so each renderer owns its material - SetPointSize/SetPointEmission write to it, and
      //multiple default-material point streams must not stomp a single shared Resources asset.
      return new Material(shared);
    }

    public virtual void Dispose()
    {
      if (pcMeshFilter != null && pcMeshFilter.sharedMesh != null)
        DestroyImmediate(pcMeshFilter.sharedMesh);
      if (pcObject != null)
        DestroyImmediate(pcObject);

      buffersInitialized = false;
      isDisposed = true;
    }

    public bool IsDisposed()
    {
      return isDisposed;
    }
  }
}
